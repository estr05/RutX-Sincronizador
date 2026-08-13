// ============================================================================
// ARCHIVO: FotoStorageService.cs
// PROPOSITO: Guarda en disco las fotografias de las no-ventas.
// La carpeta se configura en `Storage:FotosPath` (appsettings.json); si no
// esta configurada, usa `Fotos` bajo el ContentRoot (directorio del exe).
// La referencia guardada en DOCTOS_PV.DESCRIPCION es el NOMBRE del archivo
// (ej. `VTA-AB12CD34.jpg`), no la ruta completa.
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Services;

public partial class FotoStorageService : IFotoStorageService
{
    private readonly ILogger<FotoStorageService> _logger;
    private readonly string _carpeta;

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

        Directory.CreateDirectory(_carpeta);
    }

    public string Carpeta => _carpeta;

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
