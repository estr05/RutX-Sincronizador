using Microsoft.AspNetCore.Http;
using Rutx.Sincronizador.Data.Web;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Almacenamiento de las fotografias de las no-ventas en disco.
/// La carpeta raíz se configura en `Storage:FotosPath` (appsettings.json).
/// La subcarpeta `.staging\` almacena temporalmente los archivos durante la
/// saga antes de su promoción a la carpeta definitiva.
/// </summary>
public interface IFotoStorageService
{
    /// <summary>Ruta absoluta de la carpeta de fotos finalizadas.</summary>
    string Carpeta { get; }

    // ────────────────────────────────────────────────────────────────────────
    // API LEGACY (compatibilidad con endpoints que no usan la saga)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guarda [foto] como `{ventaMovilId}.jpg` en la carpeta de fotos.
    /// Retorna el nombre de archivo (referencia para DOCTOS_PV) o null si
    /// no se adjunto foto.
    /// </summary>
    Task<string?> GuardarAsync(IFormFile? foto, string ventaMovilId);

    /// <summary>
    /// Retorna el archivo de la foto por su nombre (para servirlo), o null.
    /// No verifica estado en BD/C — solo para uso interno sin saga.
    /// </summary>
    FileInfo? Obtener(string nombreArchivo);

    // ────────────────────────────────────────────────────────────────────────
    // API DE LA SAGA
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Guarda el archivo en la carpeta .staging\ y calcula su SHA-256.
    /// El nombre físico se genera por el servidor (nvop-{operationId}-{ticks}.{ext}).
    /// Extensiones permitidas: jpg, jpeg, png, webp.
    /// </summary>
    Task<StagingResult> GuardarStagingAsync(
        IFormFile foto,
        long operationId,
        string extension,
        CancellationToken ct = default);

    /// <summary>
    /// Mueve el archivo de .staging\ a la carpeta de fotos finalizadas.
    /// Devuelve el relativePath final (relativo a la carpeta raíz).
    /// </summary>
    Task<string> PromoverAsync(string storedName, CancellationToken ct = default);

    /// <summary>
    /// Elimina un archivo de .staging\ (limpieza si la saga falla antes de Firebird).
    /// No lanza excepción si el archivo no existe.
    /// </summary>
    Task EliminarStagingAsync(string storedName, CancellationToken ct = default);

    /// <summary>
    /// Busca un archivo completado usando BD/C como fuente de verdad.
    /// - Verifica que stored_name exista en rutx_media_files y status = completed.
    /// - Valida que la ruta física esté dentro de la carpeta raíz (anti-traversal).
    /// - Devuelve el FileInfo y el MIME almacenado en BD/C.
    /// - Devuelve null si no existe o no está completo.
    /// </summary>
    Task<(FileInfo File, string MimeType)?> ObtenerFotoCompletadaAsync(
        string storedName,
        IWebSqliteStore store,
        CancellationToken ct = default);
}

/// <summary>
/// Resultado de guardar un archivo en staging.
/// </summary>
public sealed record StagingResult(
    string StoredName,
    string RelativePath,
    long SizeBytes,
    string Sha256);
