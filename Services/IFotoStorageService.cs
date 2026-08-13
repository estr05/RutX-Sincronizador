using Microsoft.AspNetCore.Http;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Almacenamiento de las fotografias de las no-ventas en disco.
/// La carpeta se configura en `Storage:FotosPath` (appsettings.json).
/// </summary>
public interface IFotoStorageService
{
    /// <summary>
    /// Guarda [foto] como `&lt;ventaMovilId&gt;.jpg` en la carpeta de fotos.
    /// Retorna el nombre de archivo (referencia para DOCTOS_PV) o null si
    /// no se adjunto foto.
    /// </summary>
    Task<string?> GuardarAsync(IFormFile? foto, string ventaMovilId);

    /// <summary>Ruta absoluta de la carpeta de fotos (la crea si no existe).</summary>
    string Carpeta { get; }

    /// <summary>
    /// Retorna el archivo de la foto por su nombre (para servirlo), o null.
    /// </summary>
    FileInfo? Obtener(string nombreArchivo);
}
