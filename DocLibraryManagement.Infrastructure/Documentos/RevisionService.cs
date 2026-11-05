using System.Security.Cryptography;
using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Documentos;

internal sealed class RevisionService(
    IDbContextFactory<AppDbContext> fabrica,
    IFileStore almacen,
    IOptions<ArchivosOptions> opciones,
    TimeProvider reloj) : IRevisionService
{
    private const int MaximoMotivo = 1000;
    private const int MaximoDescripcion = 4000;

    private const string MensajeConcurrencia =
        "La revisión cambió mientras la editabas (otra persona o el flujo la modificó). Recarga la página e inténtalo de nuevo.";

    // ---------- Consulta ----------

    public async Task<RevisionDetalleDto> ObtenerAsync(Actor actor, int revisionId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct, seguimiento: false);

        if (!await PermisosDocumento.PuedeVerRevisionAsync(db, actor, r.Documento!.Area!.Codigo, r.DocumentoId, r.Estado, ct))
        {
            throw new AccesoDenegadoException("No tienes permiso para ver esta revisión.");
        }

        string? rechazo = null;
        if (r.Estado == EstadoRevision.Borrador)
        {
            // Se muestra por qué volvió a borrador, para que el autor sepa qué corregir.
            var paso = await db.PasosInstancia.AsNoTracking()
                .Where(p => p.Instancia!.RevisionId == revisionId && p.Estado == EstadoPaso.Rechazado)
                .OrderByDescending(p => p.Instancia!.Numero)
                .Select(p => new { Quien = p.DecididoPor!.NombreVisible, p.Comentario })
                .FirstOrDefaultAsync(ct);
            rechazo = paso is null ? null : $"{paso.Quien}: {paso.Comentario}";
        }

        var totalRevisiones = await db.Revisiones.CountAsync(x => x.DocumentoId == r.DocumentoId, ct);

        return new RevisionDetalleDto(
            r.Id, r.DocumentoId, r.Documento.Codigo, r.Documento.Area!.Codigo, r.Documento.Titulo, r.Etiqueta, r.Estado,
            r.MotivoCambio, r.DescripcionCambio,
            r.RutaArchivoPdf is not null, r.HashArchivoPdf,
            r.RutaArchivoFuente is not null, r.HashArchivoFuente,
            r.AutorId, r.Autor!.NombreVisible, r.FechaCreacionUtc, r.FechaAprobacionUtc,
            rechazo, EsPrimeraEmision: totalRevisiones == 1 && r.Mayor == 0 && r.Menor == 0,
            r.Documento.TipoDocumentoId);
    }

    // ---------- Edición del borrador ----------

    public async Task ActualizarBorradorAsync(Actor actor, int revisionId, string motivoCambio, string? descripcionCambio, CancellationToken ct = default)
    {
        var motivo = (motivoCambio ?? "").Trim();
        var descripcion = string.IsNullOrWhiteSpace(descripcionCambio) ? null : descripcionCambio.Trim();

        if (motivo.Length == 0)
        {
            throw new ReglaNegocioException("El motivo del cambio es obligatorio.");
        }

        if (motivo.Length > MaximoMotivo)
        {
            throw new ReglaNegocioException($"El motivo no puede pasar de {MaximoMotivo} caracteres.");
        }

        if (descripcion is { Length: > MaximoDescripcion })
        {
            throw new ReglaNegocioException($"La descripción no puede pasar de {MaximoDescripcion} caracteres.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct);
        ExigirBorradorEditable(actor, r);

        var anterior = $"Motivo={r.MotivoCambio}; Descripcion={r.DescripcionCambio}";
        r.MotivoCambio = motivo;
        r.DescripcionCambio = descripcion;
        Bitacora.Registrar(db, reloj, actor, "Revision.ActualizarBorrador", nameof(Documento), r.Documento!.Codigo,
            anterior, $"Motivo={motivo}; Descripcion={descripcion}", comentario: r.Etiqueta);

        await GuardarAsync(db, ct);
    }

    // ---------- Archivos ----------

    public async Task SubirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, string nombreOriginal, Stream contenido, CancellationToken ct = default)
    {
        var extension = ReglasArchivo.ValidarExtension(tipo, nombreOriginal);

        // Primera comprobación, barata, antes de recibir un archivo grande.
        await using (var previa = await fabrica.CreateDbContextAsync(ct))
        {
            ExigirBorradorEditable(actor, await CargarAsync(previa, revisionId, ct, seguimiento: false));
        }

        var guardado = await almacen.GuardarAsync(
            contenido, extension, opciones.Value.TamanoMaximoBytes,
            cabecera => ReglasArchivo.ValidarCabecera(extension, cabecera), ct);

        string? rutaAnterior = null;
        try
        {
            // Se vuelve a leer: durante la subida el flujo pudo haber iniciado y el archivo ya no sería editable.
            await using var db = await fabrica.CreateDbContextAsync(ct);
            var r = await CargarAsync(db, revisionId, ct);
            ExigirBorradorEditable(actor, r);

            if (tipo == TipoArchivo.Pdf)
            {
                rutaAnterior = r.RutaArchivoPdf;
                r.RutaArchivoPdf = guardado.Ruta;
                r.HashArchivoPdf = guardado.HashSha256;
            }
            else
            {
                rutaAnterior = r.RutaArchivoFuente;
                r.RutaArchivoFuente = guardado.Ruta;
                r.HashArchivoFuente = guardado.HashSha256;
            }

            Bitacora.Registrar(db, reloj, actor, tipo == TipoArchivo.Pdf ? "Revision.SubirPdf" : "Revision.SubirFuente",
                nameof(Documento), r.Documento!.Codigo, nuevo: $"{r.Etiqueta}; SHA256={guardado.HashSha256}; Bytes={guardado.Bytes}");

            await GuardarAsync(db, ct);
        }
        catch
        {
            // Si no quedó referenciado en la BD, el archivo recién escrito es basura: se retira.
            await almacen.EliminarAsync(guardado.Ruta, CancellationToken.None);
            throw;
        }

        if (rutaAnterior is not null)
        {
            await almacen.EliminarAsync(rutaAnterior, CancellationToken.None); // el borrador reemplazado ya no se necesita
        }
    }

    public async Task<ArchivoParaDescarga> AbrirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct);
        var codigoArea = r.Documento!.Area!.Codigo;

        // El PDF sigue la regla de la revisión; la fuente editable es solo de gestores.
        var autorizado = tipo == TipoArchivo.Pdf
            ? await PermisosDocumento.PuedeVerRevisionAsync(db, actor, codigoArea, r.DocumentoId, r.Estado, ct)
            : PermisosDocumento.EsGestor(actor, codigoArea);
        if (!autorizado)
        {
            throw new AccesoDenegadoException("No tienes permiso para descargar este archivo.");
        }

        var ruta = tipo == TipoArchivo.Pdf ? r.RutaArchivoPdf : r.RutaArchivoFuente;
        var hashEsperado = tipo == TipoArchivo.Pdf ? r.HashArchivoPdf : r.HashArchivoFuente;
        if (ruta is null || hashEsperado is null)
        {
            throw new ReglaNegocioException(tipo == TipoArchivo.Pdf ? "Esta revisión todavía no tiene PDF." : "Esta revisión no tiene fuente editable.");
        }

        Stream flujo;
        try
        {
            flujo = almacen.Abrir(ruta);
        }
        catch (FileNotFoundException)
        {
            await RegistrarFalloIntegridadAsync(db, actor, r, tipo, "archivo no encontrado", ct);
            throw new ReglaNegocioException("El archivo no está en el almacén. Avisa a IT.");
        }

        var hashReal = Convert.ToHexString(await SHA256.HashDataAsync(flujo, ct)).ToLowerInvariant();
        if (hashReal != hashEsperado)
        {
            await flujo.DisposeAsync();
            await RegistrarFalloIntegridadAsync(db, actor, r, tipo, "el hash no coincide", ct);
            throw new ReglaNegocioException("El archivo no pasó la verificación de integridad. Avisa a IT.");
        }

        flujo.Position = 0;

        if (r.Estado == EstadoRevision.Obsoleto)
        {
            Bitacora.Registrar(db, reloj, actor, "Revision.DescargarObsoleta", nameof(Documento), r.Documento.Codigo, comentario: r.Etiqueta);
            await db.SaveChangesAsync(ct);
        }

        var extension = Path.GetExtension(ruta);
        var nombre = $"{r.Documento.Codigo}_{r.Etiqueta}{extension}";
        return new ArchivoParaDescarga(flujo, nombre, ReglasArchivo.TipoContenido(extension), EnLinea: tipo == TipoArchivo.Pdf);
    }

    private async Task RegistrarFalloIntegridadAsync(AppDbContext db, Actor actor, RevisionDocumento r, TipoArchivo tipo, string motivo, CancellationToken ct)
    {
        Bitacora.Registrar(db, reloj, actor, "Archivo.IntegridadFallida", nameof(Documento), r.Documento!.Codigo,
            comentario: $"{r.Etiqueta} {tipo}: {motivo}");
        await db.SaveChangesAsync(ct);
    }

    // ---------- Cancelación ----------

    public async Task CancelarDocumentoNuevoAsync(Actor actor, int documentoId, string motivo, CancellationToken ct = default)
    {
        var texto = (motivo ?? "").Trim();
        if (texto.Length == 0)
        {
            throw new ReglaNegocioException("Indica el motivo de la cancelación.");
        }

        if (texto.Length > MaximoMotivo)
        {
            throw new ReglaNegocioException($"El motivo no puede pasar de {MaximoMotivo} caracteres.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var doc = await db.Documentos.Include(d => d.Area).Include(d => d.Revisiones)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (!actor.PuedeEditarEnArea(doc.Area!.Codigo))
        {
            throw new AccesoDenegadoException($"No tienes permiso para modificar documentos del área {doc.Area.Codigo}.");
        }

        if (doc.Estado == EstadoDocumento.Retirado)
        {
            throw new ReglaNegocioException("El documento ya está retirado.");
        }

        if (doc.Revisiones.Count != 1
            || doc.Revisiones[0] is not { Mayor: 0, Menor: 0, Estado: EstadoRevision.Borrador } revision)
        {
            throw new ReglaNegocioException(
                "Solo se puede cancelar un documento nuevo cuya primera revisión sigue en borrador. Si ya entró al flujo, cancela primero el flujo.");
        }

        revision.Estado = EstadoRevision.Cancelado;
        doc.Estado = EstadoDocumento.Retirado;
        Bitacora.Registrar(db, reloj, actor, "Documento.CancelarNuevo", nameof(Documento), doc.Codigo,
            anterior: "Activo; Rev.00 Borrador", nuevo: "Retirado; Rev.00 Cancelado", comentario: texto);

        await GuardarAsync(db, ct);
    }

    // ---------- Apoyo ----------

    private static async Task<RevisionDocumento> CargarAsync(AppDbContext db, int id, CancellationToken ct, bool seguimiento = true)
    {
        var consulta = db.Revisiones
            .Include(r => r.Documento!).ThenInclude(d => d.Area)
            .Include(r => r.Autor)
            .AsQueryable();
        if (!seguimiento)
        {
            consulta = consulta.AsNoTracking();
        }

        return await consulta.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new ReglaNegocioException("La revisión no existe.");
    }

    private static void ExigirBorradorEditable(Actor actor, RevisionDocumento r)
    {
        if (!actor.PuedeEditarEnArea(r.Documento!.Area!.Codigo))
        {
            throw new AccesoDenegadoException($"No tienes permiso para modificar documentos del área {r.Documento.Area.Codigo}.");
        }

        if (r.Documento.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado y no admite cambios.");
        }

        if (r.Estado != EstadoRevision.Borrador)
        {
            throw new ReglaNegocioException("Solo se puede modificar una revisión en borrador.");
        }
    }

    private static async Task GuardarAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ReglaNegocioException(MensajeConcurrencia);
        }
    }
}
