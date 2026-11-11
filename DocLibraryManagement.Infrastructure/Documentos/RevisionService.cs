using System.Security.Cryptography;
using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Correo;
using DocLibraryManagement.Infrastructure.Persistencia;
using DocLibraryManagement.Infrastructure.Vencimientos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Documentos;

internal sealed class RevisionService(
    IDbContextFactory<AppDbContext> fabrica,
    IFileStore almacen,
    IOptions<ArchivosOptions> opciones,
    IExtractorTexto extractor,
    IEncoladorNotificaciones notificador,
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

        string? devolucion = null;
        var revocada = false;
        if (r.Estado == EstadoRevision.Borrador)
        {
            // Por qué volvió a borrador, para que el autor sepa qué corregir. Se mira SOLO el último envío: un rechazo
            // viejo no debe aparecer si después se reenvió y se canceló, porque sería un motivo que ya no aplica.
            var ultimo = await db.InstanciasFlujo.AsNoTracking()
                .Where(i => i.RevisionId == revisionId)
                .OrderByDescending(i => i.Numero)
                .Select(i => new
                {
                    i.Estado,
                    i.MotivoCierre,
                    Rechazo = i.Pasos.Where(p => p.Estado == EstadoPaso.Rechazado)
                        .Select(p => new { Quien = p.DecididoPor!.NombreVisible, p.Comentario })
                        .FirstOrDefault(),
                })
                .FirstOrDefaultAsync(ct);

            if (ultimo is { Estado: EstadoFlujo.Rechazado, Rechazo: { } rechazo })
            {
                devolucion = $"{rechazo.Quien}: {rechazo.Comentario}";
            }
            else if (ultimo is { Estado: EstadoFlujo.Revocado })
            {
                devolucion = ultimo.MotivoCierre;
                revocada = true;
            }
        }

        var totalRevisiones = await db.Revisiones.CountAsync(x => x.DocumentoId == r.DocumentoId, ct);

        return new RevisionDetalleDto(
            r.Id, r.DocumentoId, r.Documento.Codigo, r.Documento.Area!.Codigo, r.Documento.Titulo, r.Etiqueta, r.Estado,
            r.MotivoCambio, r.DescripcionCambio,
            r.RutaArchivoPdf is not null, r.HashArchivoPdf,
            r.RutaArchivoFuente is not null, r.HashArchivoFuente,
            r.AutorId, r.Autor!.NombreVisible, r.FechaCreacionUtc, r.FechaAprobacionUtc,
            devolucion, revocada, EsPrimeraEmision: totalRevisiones == 1 && r.Mayor == 0 && r.Menor == 0,
            r.Documento.TipoDocumentoId, r.Mayor, r.Menor, r.FechaVigenciaUtc, r.FechaProximaRevisionUtc);
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

    public async Task<ResultadoSubida> SubirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, string nombreOriginal, Stream contenido, CancellationToken ct = default)
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
        ExtraccionTexto? extraccion = null;
        try
        {
            // El texto se lee del archivo ya guardado (no del flujo de subida, que no se puede releer).
            if (tipo == TipoArchivo.Pdf)
            {
                await using var lectura = almacen.Abrir(guardado.Ruta);
                extraccion = await extractor.ExtraerAsync(lectura, ct);
            }

            // Se vuelve a leer: durante la subida el flujo pudo haber iniciado y el archivo ya no sería editable.
            await using var db = await fabrica.CreateDbContextAsync(ct);
            var r = await CargarAsync(db, revisionId, ct);
            ExigirBorradorEditable(actor, r);

            if (tipo == TipoArchivo.Pdf)
            {
                rutaAnterior = r.RutaArchivoPdf;
                r.RutaArchivoPdf = guardado.Ruta;
                r.HashArchivoPdf = guardado.HashSha256;
                r.TextoExtraido = extraccion!.Texto; // "" = leído pero sin texto; reemplazar el PDF reemplaza también el texto
            }
            else
            {
                rutaAnterior = r.RutaArchivoFuente;
                r.RutaArchivoFuente = guardado.Ruta;
                r.HashArchivoFuente = guardado.HashSha256;
            }

            Bitacora.Registrar(db, reloj, actor, tipo == TipoArchivo.Pdf ? "Revision.SubirPdf" : "Revision.SubirFuente",
                nameof(Documento), r.Documento!.Codigo,
                nuevo: $"{r.Etiqueta}; SHA256={guardado.HashSha256}; Bytes={guardado.Bytes}"
                       + (extraccion is null ? "" : $"; TextoBuscable={(extraccion.TieneTexto ? "sí" : "no")}"));

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

        return extraccion switch
        {
            null => new ResultadoSubida(false, null), // la fuente editable no se indexa
            { TieneTexto: true } => new ResultadoSubida(true, null),
            { PudoLeerse: false } => new ResultadoSubida(false,
                "No se pudo leer el contenido del PDF (¿está dañado o protegido con contraseña?). Puedes enviarlo, pero no se encontrará al buscar por contenido."),
            _ => new ResultadoSubida(false,
                "Este PDF no contiene texto seleccionable (parece escaneado). Puedes enviarlo, pero no se encontrará al buscar por contenido."),
        };
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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // También si falta la carpeta entera del almacén (p. ej. una unidad de red desmontada).
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

    // ---------- Nuevas revisiones ----------

    public async Task<RevisionDetalleDto> CrearRevisionAsync(
        Actor actor, int documentoId, TipoRevisionNueva tipo, string motivoCambio, string? descripcionCambio,
        bool declaraQueNoAlteraRequisitos, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(tipo))
        {
            throw new ReglaNegocioException("El tipo de revisión no es válido.");
        }

        var motivo = NormalizarMotivo(motivoCambio, "El motivo del cambio es obligatorio.");
        var descripcion = string.IsNullOrWhiteSpace(descripcionCambio) ? null : descripcionCambio.Trim();
        if (descripcion is { Length: > MaximoDescripcion })
        {
            throw new ReglaNegocioException($"La descripción no puede pasar de {MaximoDescripcion} caracteres.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var doc = await db.Documentos
            .Include(d => d.Area!).ThenInclude(a => a.Dueno)
            .Include(d => d.Revisiones)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (!actor.PuedeEditarEnArea(doc.Area!.Codigo))
        {
            throw new AccesoDenegadoException($"No tienes permiso para modificar documentos del área {doc.Area.Codigo}.");
        }

        if (doc.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado y no admite revisiones nuevas.");
        }

        if (doc.Revisiones.FirstOrDefault(r => r.Estado.EsAbierta()) is { } abierta)
        {
            throw new ReglaNegocioException(
                $"Ya hay una revisión en curso ({abierta.Etiqueta}, {abierta.Estado}). Termínala o descártala antes de abrir otra.");
        }

        var vigente = doc.Revisiones.FirstOrDefault(r => r.Estado == EstadoRevision.Vigente)
            ?? throw new ReglaNegocioException("El documento no tiene una revisión vigente de la cual partir.");

        if (tipo == TipoRevisionNueva.Menor)
        {
            if (!declaraQueNoAlteraRequisitos)
            {
                throw new ReglaNegocioException(
                    "Para una revisión menor confirma que el cambio no altera requisitos, pasos ni responsabilidades. Si los altera, abre una revisión mayor.");
            }

            if (doc.Area.Dueno is null)
            {
                throw new ReglaNegocioException(
                    $"El área {doc.Area.Codigo} no tiene un dueño designado para aprobar revisiones menores. Pide a un Administrador que lo designe.");
            }
        }

        var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct)
            ?? throw new ReglaNegocioException("Tu usuario aún no está registrado. Cierra sesión y vuelve a entrar.");

        var (mayor, menor) = ReglasRevision.SiguienteNumero(vigente.Mayor, vigente.Menor, tipo);
        var nueva = new RevisionDocumento
        {
            DocumentoId = doc.Id,
            Mayor = mayor,
            Menor = menor,
            Estado = EstadoRevision.Borrador,
            MotivoCambio = motivo,
            DescripcionCambio = descripcion,
            AutorId = usuario.Id,
            FechaCreacionUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Revisiones.Add(nueva);

        Bitacora.Registrar(db, reloj, actor, "Revision.Crear", nameof(Documento), doc.Codigo,
            anterior: vigente.Etiqueta, nuevo: $"{nueva.Etiqueta} Borrador ({(tipo == TipoRevisionNueva.Menor ? "menor" : "mayor")})",
            comentario: tipo == TipoRevisionNueva.Menor ? $"Declara que no altera requisitos, pasos ni responsabilidades. Motivo: {motivo}" : motivo);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            // Dos personas abrieron revisión a la vez: el índice de «una sola revisión abierta» deja pasar a una.
            throw new ReglaNegocioException("Otra persona abrió una revisión de este documento al mismo tiempo. Recarga la página.");
        }

        return await ObtenerAsync(actor, nueva.Id, ct);
    }

    public async Task CancelarBorradorAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default)
    {
        var texto = NormalizarMotivo(motivo, "Indica el motivo del descarte.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct);
        ExigirBorradorEditable(actor, r);

        if (!await db.Revisiones.AnyAsync(x => x.DocumentoId == r.DocumentoId && x.Id != r.Id, ct))
        {
            throw new ReglaNegocioException(
                "Es la primera emisión del documento: para descartarla usa «Cancelar este documento», que también retira el código.");
        }

        r.Estado = EstadoRevision.Cancelado;
        Bitacora.Registrar(db, reloj, actor, "Revision.CancelarBorrador", nameof(Documento), r.Documento!.Codigo,
            anterior: $"{r.Etiqueta} Borrador", nuevo: $"{r.Etiqueta} Cancelado", comentario: texto);

        await GuardarAsync(db, ct);
    }

    // ---------- Revisión aprobada que aún no entra en vigor ----------

    public async Task ReprogramarVigenciaAsync(Actor actor, int revisionId, DateTime nuevaVigenciaUtc, string motivo, CancellationToken ct = default)
    {
        ExigirAdministrador(actor, "cambiar la fecha de vigencia");
        var texto = NormalizarMotivo(motivo, "Indica el motivo del cambio de fecha.");

        // Reutiliza la regla de vigencia: una fecha que ya pasó significaría «publicar ya», y eso aquí NO se permite.
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var (vigencia, inmediata) = ReglasRevision.ResolverVigencia(ahora, nuevaVigenciaUtc);
        if (inmediata)
        {
            throw new ReglaNegocioException("La nueva fecha debe ser posterior a este momento.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct);
        ExigirAprobadaPendiente(r);

        var anterior = r.FechaVigenciaUtc;
        r.FechaVigenciaUtc = vigencia;
        Bitacora.Registrar(db, reloj, actor, "Revision.ReprogramarVigencia", nameof(Documento), r.Documento!.Codigo,
            anterior: $"{r.Etiqueta} entraba en vigor el {anterior:yyyy-MM-dd HH:mm} UTC",
            nuevo: $"{r.Etiqueta} entrará en vigor el {vigencia:yyyy-MM-dd HH:mm} UTC", comentario: texto);

        // Avisan el autor de la revisión y el responsable del documento (una vez si son la misma persona), menos quien actuó.
        var valores = notificador.ValoresBase(r.Documento, r.Etiqueta);
        valores["Actor"] = await notificador.NombreVisibleAsync(db, actor.Identificador, ct);
        valores["Fecha"] = notificador.Fecha(vigencia);
        valores["Motivo"] = texto;
        foreach (var destinatario in new[] { r.AutorId, r.Documento.ResponsableId }.Distinct())
        {
            await notificador.EncolarAsync(db, EventosCorreo.VigenciaReprogramada, destinatario, valores, r.Documento.Codigo,
                omitirIdentificador: actor.Identificador, ct: ct);
        }

        await GuardarAsync(db, ct);
    }

    public async Task RevocarAprobacionAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default)
    {
        ExigirAdministrador(actor, "anular una aprobación");
        var texto = NormalizarMotivo(motivo, "Indica el motivo por el que se anula la aprobación.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarAsync(db, revisionId, ct);
        ExigirAprobadaPendiente(r);

        // El envío que terminó aprobado queda en el historial como «Revocado»: las decisiones de sus etapas se conservan.
        var flujo = await db.InstanciasFlujo
            .Where(i => i.RevisionId == revisionId && i.Estado == EstadoFlujo.Aprobado)
            .OrderByDescending(i => i.Numero)
            .FirstOrDefaultAsync(ct)
            ?? throw new ReglaNegocioException("La revisión no tiene un flujo aprobado que anular.");

        var programada = r.FechaVigenciaUtc;
        flujo.Estado = EstadoFlujo.Revocado;
        flujo.MotivoCierre = texto;

        r.Estado = EstadoRevision.Borrador;
        r.FechaAprobacionUtc = null;
        r.FechaVigenciaUtc = null;

        Bitacora.Registrar(db, reloj, actor, "Revision.RevocarAprobacion", nameof(Documento), r.Documento!.Codigo,
            anterior: $"{r.Etiqueta} Aprobado (entraba en vigor el {programada:yyyy-MM-dd HH:mm} UTC)",
            nuevo: $"{r.Etiqueta} Borrador; envío #{flujo.Numero} revocado", comentario: texto);

        // El autor debe enterarse: la revisión volvió a sus manos y tiene que corregirla y reenviarla.
        var valores = notificador.ValoresBase(r.Documento, r.Etiqueta);
        valores["Actor"] = await notificador.NombreVisibleAsync(db, actor.Identificador, ct);
        valores["Motivo"] = texto;
        await notificador.EncolarAsync(db, EventosCorreo.AprobacionRevocada, r.AutorId, valores, r.Documento.Codigo,
            omitirIdentificador: actor.Identificador, ct: ct);

        await GuardarAsync(db, ct);
    }

    private static void ExigirAdministrador(Actor actor, string accion)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException($"Solo un Administrador puede {accion}.");
        }
    }

    /// <summary>Solo se interviene una revisión que ya fue aprobada pero todavía no está en vigor.</summary>
    private static void ExigirAprobadaPendiente(RevisionDocumento r)
    {
        if (r.Estado != EstadoRevision.Aprobado)
        {
            throw new ReglaNegocioException(
                "Solo se puede hacer esto con una revisión aprobada que todavía no entra en vigor. "
                + $"La {r.Etiqueta} está en estado {r.Estado}.");
        }

        if (r.Documento!.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado.");
        }
    }

    public async Task RetirarDocumentoAsync(Actor actor, int documentoId, string motivo, CancellationToken ct = default)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede retirar un documento.");
        }

        var texto = NormalizarMotivo(motivo, "Indica el motivo del retiro.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var doc = await db.Documentos.Include(d => d.Revisiones)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (doc.Estado == EstadoDocumento.Retirado)
        {
            throw new ReglaNegocioException("El documento ya está retirado.");
        }

        var cambios = new List<string>();
        var idsAbiertas = doc.Revisiones.Where(r => r.Estado.EsAbierta()).Select(r => r.Id).ToList();

        // Los flujos en curso se cierran junto con su revisión; si no, quedarían tareas pendientes huérfanas.
        var flujos = await db.InstanciasFlujo.Include(i => i.Pasos)
            .Where(i => idsAbiertas.Contains(i.RevisionId) && i.Estado == EstadoFlujo.EnCurso)
            .ToListAsync(ct);
        foreach (var flujo in flujos)
        {
            flujo.Estado = EstadoFlujo.Cancelado;
            flujo.FechaFinUtc = reloj.GetUtcNow().UtcDateTime;
            foreach (var paso in flujo.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente))
            {
                paso.Estado = EstadoPaso.Omitido;
            }
        }

        foreach (var rev in doc.Revisiones)
        {
            if (rev.Estado == EstadoRevision.Vigente)
            {
                rev.Estado = EstadoRevision.Obsoleto;
                cambios.Add($"{rev.Etiqueta} Vigente→Obsoleto");
            }
            else if (rev.Estado.EsAbierta())
            {
                cambios.Add($"{rev.Etiqueta} {rev.Estado}→Cancelado");
                rev.Estado = EstadoRevision.Cancelado;
            }
        }

        await SolicitudesSinCambiosCierre.CancelarPendientesAsync(db, reloj, actor,
            [.. doc.Revisiones.Where(r => r.Estado == EstadoRevision.Obsoleto).Select(r => r.Id)], doc.Codigo, "El documento fue retirado.", ct);

        doc.Estado = EstadoDocumento.Retirado;
        Bitacora.Registrar(db, reloj, actor, "Documento.Retirar", nameof(Documento), doc.Codigo,
            anterior: "Activo", nuevo: $"Retirado; {(cambios.Count == 0 ? "sin revisiones afectadas" : string.Join("; ", cambios))}",
            comentario: texto);

        await GuardarAsync(db, ct);
    }

    private static string NormalizarMotivo(string? motivo, string mensajeObligatorio)
    {
        var limpio = (motivo ?? "").Trim();
        if (limpio.Length == 0)
        {
            throw new ReglaNegocioException(mensajeObligatorio);
        }

        if (limpio.Length > MaximoMotivo)
        {
            throw new ReglaNegocioException($"El motivo no puede pasar de {MaximoMotivo} caracteres.");
        }

        return limpio;
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
