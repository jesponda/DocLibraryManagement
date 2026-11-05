using System.Security.Cryptography;
using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Archivos;

/// <summary>
/// Almacén en una carpeta local o de red. Los archivos se guardan con nombre generado (GUID) dentro de
/// carpetas año/mes, nunca con el nombre que trae el usuario, y siempre fuera de la raíz web.
/// </summary>
internal sealed class FileStoreEnDisco(IOptions<ArchivosOptions> opciones, TimeProvider reloj) : IFileStore
{
    private string Raiz
    {
        get
        {
            var ruta = opciones.Value.RutaBase;
            if (string.IsNullOrWhiteSpace(ruta))
            {
                throw new InvalidOperationException("Falta configurar 'Archivos:RutaBase' (carpeta del almacén de archivos).");
            }

            return Path.GetFullPath(ruta);
        }
    }

    public async Task<ArchivoGuardado> GuardarAsync(
        Stream contenido,
        string extension,
        long maximoBytes,
        Action<byte[]> validarCabecera,
        CancellationToken ct = default)
    {
        // La extensión se usa para armar el nombre en disco: solo se admite lo conocido y simple.
        if (extension.Length is < 2 or > 6 || extension[0] != '.' || !extension.Skip(1).All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            throw new ReglaNegocioException("La extensión del archivo no es válida.");
        }

        var ahora = reloj.GetUtcNow();
        var relativa = $"{ahora:yyyy}/{ahora:MM}/{Guid.NewGuid():N}{extension}";
        var destino = Resolver(relativa);
        var temporal = destino + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        long total = 0;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var salida = new FileStream(temporal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];

                // Se juntan los primeros bytes (la lectura puede devolver menos de los pedidos)
                // para validar la firma antes de seguir escribiendo.
                var cabecera = new byte[ReglasArchivo.BytesDeCabecera];
                var enCabecera = 0;
                while (enCabecera < cabecera.Length)
                {
                    var n = await contenido.ReadAsync(cabecera.AsMemory(enCabecera), ct);
                    if (n == 0)
                    {
                        break;
                    }

                    enCabecera += n;
                }

                validarCabecera(cabecera[..enCabecera]);
                await Escribir(cabecera.AsMemory(0, enCabecera));

                int leidos;
                while ((leidos = await contenido.ReadAsync(buffer, ct)) > 0)
                {
                    await Escribir(buffer.AsMemory(0, leidos));
                }

                async Task Escribir(ReadOnlyMemory<byte> bloque)
                {
                    total += bloque.Length;
                    if (total > maximoBytes)
                    {
                        throw new ReglaNegocioException($"El archivo supera el tamaño máximo de {maximoBytes / (1024 * 1024)} MB.");
                    }

                    hash.AppendData(bloque.Span);
                    await salida.WriteAsync(bloque, ct);
                }
            }

            if (total == 0)
            {
                throw new ReglaNegocioException("El archivo está vacío.");
            }

            File.Move(temporal, destino);
            return new ArchivoGuardado(relativa, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
        }
        catch
        {
            // Cualquier fallo (validación, tamaño, cancelación, disco) no deja basura a medio escribir.
            TryBorrar(temporal);
            throw;
        }
    }

    public Stream Abrir(string ruta) =>
        new FileStream(Resolver(ruta), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);

    public Task EliminarAsync(string ruta, CancellationToken ct = default)
    {
        TryBorrar(Resolver(ruta));
        return Task.CompletedTask;
    }

    /// <summary>Convierte una ruta relativa en absoluta garantizando que no se salga de la raíz del almacén.</summary>
    private string Resolver(string relativa)
    {
        var raiz = Raiz;
        var completa = Path.GetFullPath(Path.Combine(raiz, relativa));
        var prefijo = raiz.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!completa.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ruta de archivo fuera del almacén.");
        }

        return completa;
    }

    private static void TryBorrar(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (IOException)
        {
            // Mejor esfuerzo: un temporal huérfano no afecta la integridad.
        }
    }
}
