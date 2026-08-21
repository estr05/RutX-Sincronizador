// ============================================================================
// ARCHIVO: FotoStorageService.cs
// PROPOSITO: Guarda en disco las fotografias de las no-ventas.
// La carpeta raíz se configura en `Storage:FotosPath` (appsettings.json).
// La subcarpeta .staging\ almacena temporalmente durante la saga; la carpeta
// raíz contiene las fotos completadas (promovidas).
// ============================================================================

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data.Web;

namespace Rutx.Sincronizador.Services;

public partial class FotoStorageService : IFotoStorageService
{
    private readonly ILogger<FotoStorageService> _logger;
    private readonly string _carpeta;
    private readonly string _carpetaStaging;

    // Extensiones permitidas para archivos de foto (lowercase sin punto).
    private static readonly HashSet<string> ExtensionesPermitidas =
        new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "webp" };

    // Mapa MIME para las extensiones permitidas.
    private static readonly Dictionary<string, string> MimePorExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        { "jpg",  "image/jpeg" },
        { "jpeg", "image/jpeg" },
        { "png",  "image/png"  },
        { "webp", "image/webp" },
    };

    public FotoStorageService(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<FotoStorageService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configurada = configuration.GetValue<string>("Storage:FotosPath");
        _carpeta = string.IsNullOrWhiteSpace(configurada)
            ? Path.Combine(environment.ContentRootPath, "Fotos")
            : configurada;

        _carpetaStaging = Path.Combine(_carpeta, ".staging");

        Directory.CreateDirectory(_carpeta);
        Directory.CreateDirectory(_carpetaStaging);
    }

    public string Carpeta => _carpeta;

    // ────────────────────────────────────────────────────────────────────────
    // API LEGACY
    // ────────────────────────────────────────────────────────────────────────

    public async Task<string?> GuardarAsync(IFormFile? foto, string ventaMovilId)
    {
        if (foto == null || foto.Length == 0)
            return null;

        var nombre = NombreSeguro(ventaMovilId) + ".jpg";
        var ruta = Path.Combine(_carpeta, nombre);

        try
        {
            await using (var stream = new FileStream(ruta, FileMode.Create, FileAccess.Write))
            {
                await foto.CopyToAsync(stream);
            }
            _logger.LogInformation("[Fotos] Guardada {Nombre} ({Bytes} bytes) en {Carpeta}",
                nombre, foto.Length, _carpeta);
            return nombre;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Fotos] Error al guardar {Nombre}", nombre);
            return null;
        }
    }

    public FileInfo? Obtener(string nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            return null;

        var nombre = Path.GetFileName(nombreArchivo); // evita traversal
        var ruta = Path.Combine(_carpeta, nombre);
        if (!File.Exists(ruta))
            return null;
        return new FileInfo(ruta);
    }

    // ────────────────────────────────────────────────────────────────────────
    // API DE LA SAGA
    // ────────────────────────────────────────────────────────────────────────

    public async Task<StagingResult> GuardarStagingAsync(
        IFormFile foto, long operationId, string extension, CancellationToken ct = default)
    {
        // Validar extensión
        var ext = extension.TrimStart('.').ToLowerInvariant();
        if (!ExtensionesPermitidas.Contains(ext))
            throw new ArgumentException(
                $"Extensión '{ext}' no permitida. Use: {string.Join(", ", ExtensionesPermitidas)}.",
                nameof(extension));

        // Nombre generado por el servidor (nunca del cliente)
        var storedName = $"nvop-{operationId}-{DateTime.UtcNow.Ticks}.{ext}";
        var rutaStaging = Path.Combine(_carpetaStaging, storedName);
        var relPath = Path.Combine(".staging", storedName);

        string sha256;
        long sizeBytes;

        // Guardar archivo en staging
        await using (var fs = new FileStream(rutaStaging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await foto.OpenReadStream().CopyToAsync(fs, ct);
        }

        // Calcular tamaño exacto y SHA-256 del archivo persistido
        var fi = new FileInfo(rutaStaging);
        sizeBytes = fi.Length;

        await using (var fsr = File.OpenRead(rutaStaging))
        {
            var hashBytes = await SHA256.HashDataAsync(fsr, ct);
            sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        _logger.LogInformation(
            "[Saga] Foto guardada en staging: {StoredName} ({Bytes} bytes, sha256={Sha256})",
            storedName, sizeBytes, sha256);

        return new StagingResult(storedName, relPath, sizeBytes, sha256);
    }

    public Task<string> PromoverAsync(string storedName, CancellationToken ct = default)
    {
        var origen = Path.Combine(_carpetaStaging, storedName);
        var destino = Path.Combine(_carpeta, storedName);

        if (!File.Exists(origen))
            throw new FileNotFoundException($"Archivo en staging no encontrado: {storedName}", origen);

        File.Move(origen, destino, overwrite: false);

        // relativePath final: solo el nombre del archivo (relativo a _carpeta)
        _logger.LogInformation("[Saga] Foto promovida: {StoredName}", storedName);
        return Task.FromResult(storedName);
    }

    public Task EliminarStagingAsync(string storedName, CancellationToken ct = default)
    {
        var ruta = Path.Combine(_carpetaStaging, storedName);
        try
        {
            if (File.Exists(ruta))
            {
                File.Delete(ruta);
                _logger.LogInformation("[Saga] Archivo staging eliminado: {StoredName}", storedName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Saga] No se pudo eliminar archivo staging: {StoredName}", storedName);
        }
        return Task.CompletedTask;
    }

    public async Task<(FileInfo File, string MimeType)?> ObtenerFotoCompletadaAsync(
        string storedName, IWebSqliteStore store, CancellationToken ct = default)
    {
        // 1. Buscar en BD/C por stored_name (anti-traversal: no usar ruta del cliente)
        var nombreLimpio = Path.GetFileName(storedName);
        if (string.IsNullOrWhiteSpace(nombreLimpio) || nombreLimpio != storedName)
            return null; // contiene separadores de directorio → rechazar

        var mediaFile = await store.FindMediaFileByStoredNameAsync(nombreLimpio, ct);
        if (mediaFile == null || mediaFile.Status != "completed")
            return null;

        // 2. Validar que la ruta física esté dentro de _carpeta
        var rutaAbsoluta = Path.GetFullPath(Path.Combine(_carpeta, nombreLimpio));
        if (!rutaAbsoluta.StartsWith(Path.GetFullPath(_carpeta) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return null; // path traversal detectado

        if (!File.Exists(rutaAbsoluta))
            return null;

        return (new FileInfo(rutaAbsoluta), mediaFile.MimeType);
    }

    // ────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Normaliza el id de la venta movil para usarlo como nombre de archivo:
    /// solo alfanumericos, guiones y guiones bajos (evita rutas invalidas).
    /// </summary>
    public static string NombreSeguro(string ventaMovilId)
    {
        if (string.IsNullOrWhiteSpace(ventaMovilId))
            return "no_venta";
        return CaracteresValidos().Replace(ventaMovilId.Trim(), "_");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]+")]
    private static partial Regex CaracteresValidos();
}
