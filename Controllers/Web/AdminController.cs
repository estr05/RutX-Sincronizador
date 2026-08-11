using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;
using System.Text.Json;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Panel de administracion del sincronizador (web de config bajo demanda).
/// Rutas nuevas bajo /api/v2/admin/* — NO tocan el contrato movil /api/v1/*.
/// Sin autenticacion (solo escucha local); revisar si se expone fuera de localhost.
/// </summary>
[ApiController]
[Route("api/v2/admin")]
public class AdminController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IRouteService _routeService;
    private readonly ILogger<AdminController> _logger;
    private readonly string _appSettingsPath;

    public AdminController(
        IConfiguration configuration,
        IRouteService routeService,
        IHostEnvironment environment,
        ILogger<AdminController> logger)
    {
        _configuration = configuration;
        _routeService = routeService;
        _logger = logger;

        // Resolver el MISMO archivo que el host esta leyendo: content root
        // (directorio del proyecto en dev, carpeta de publish en produccion).
        // En dev, AppContext.BaseDirectory apuntaria a bin/Debug y no coincide.
        _appSettingsPath = Path.Combine(environment.ContentRootPath, "appsettings.json");
        if (!System.IO.File.Exists(_appSettingsPath))
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (System.IO.File.Exists(fallback))
                _appSettingsPath = fallback;
        }
    }

    /// <summary>
    /// GET /api/v2/admin/config
    /// Devuelve el contenido actual de appsettings.json (BD, IDs, JWT, cola).
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        try
        {
            if (!System.IO.File.Exists(_appSettingsPath))
                return NotFound(new { message = "No se encontro appsettings.json en " + _appSettingsPath });

            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(_appSettingsPath));
            return Ok(doc.RootElement.Clone());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leyendo configuracion");
            return StatusCode(500, new { message = "No se pudo leer la configuracion: " + ex.Message });
        }
    }

    /// <summary>
    /// POST /api/v2/admin/config
    /// Guarda appsettings.json con respaldo .bak previo. El host ya tiene
    /// reloadOnChange activo, asi que la config se aplica en caliente.
    /// </summary>
    [HttpPost("config")]
    public async Task<IActionResult> SaveConfig([FromBody] JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "El cuerpo debe ser un objeto JSON." });

        // Validacion minima: las secciones criticas deben venir en el body para
        // no escribir un archivo que rompa el arranque.
        var seccionesCriticas = new[] { "ConnectionStrings", "MicrosipSettings" };
        foreach (var seccion in seccionesCriticas)
        {
            if (!config.TryGetProperty(seccion, out var v) || v.ValueKind != JsonValueKind.Object)
                return BadRequest(new { message = $"Falta la seccion '{seccion}' en el JSON." });
        }

        try
        {
            if (!System.IO.File.Exists(_appSettingsPath))
                return NotFound(new { message = "No se encontro appsettings.json en " + _appSettingsPath });

            // MERGE server-side: cargar el archivo actual y solo reemplazar las
            // secciones del body. Asi se conservan Logging, AllowedHosts y otras
            // secciones que el panel no edita.
            using var doc = JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(_appSettingsPath));
            var raiz = doc.RootElement.Clone();
            var seccionesEntrantes = config.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());

            var diccionario = new Dictionary<string, object>();
            foreach (var prop in raiz.EnumerateObject())
            {
                diccionario[prop.Name] = seccionesEntrantes.ContainsKey(prop.Name)
                    ? seccionesEntrantes[prop.Name]
                    : prop.Value.Clone();
            }
            // Secciones nuevas del body que no existian en el archivo
            foreach (var (nombre, valor) in seccionesEntrantes)
            {
                if (!diccionario.ContainsKey(nombre))
                    diccionario[nombre] = valor;
            }

            var resultado = JsonSerializer.SerializeToElement(diccionario);

            string backupPath = _appSettingsPath + ".bak";
            System.IO.File.Copy(_appSettingsPath, backupPath, overwrite: true);
            _logger.LogInformation("Respaldo de configuracion creado: {Backup}", backupPath);

            await System.IO.File.WriteAllTextAsync(_appSettingsPath, resultado.GetRawText());
            _logger.LogInformation("Configuracion guardada (aplicada en caliente).");
            return Ok(new { message = "Configuracion guardada correctamente. Respaldo .bak creado." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error guardando configuracion");
            return StatusCode(500, new { message = "No se pudo guardar la configuracion: " + ex.Message });
        }
    }

    /// <summary>
    /// GET /api/v2/admin/vendedores
    /// Lista vendedores de Microsip para el desplegable del sync matutino.
    /// </summary>
    [HttpGet("vendedores")]
    public async Task<IActionResult> GetVendedores()
    {
        var connectionString = _configuration.GetConnectionString("FirebirdConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return Ok(Array.Empty<object>());

        try
        {
            using var connection = new FbConnection(connectionString);
            await connection.OpenAsync();

            var vendedores = await connection.QueryAsync(@"
                SELECT VENDEDOR_ID, TRIM(NOMBRE) AS NOMBRE
                FROM VENDEDORES
                ORDER BY NOMBRE");

            var resultado = vendedores
                .Where(v => v.VENDEDOR_ID != null)
                .Select(v => new
                {
                    vendedor_id = Convert.ToInt32(v.VENDEDOR_ID),
                    nombre = v.NOMBRE?.ToString() ?? ""
                })
                .ToList();

            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listando vendedores");
            return Ok(Array.Empty<object>()); // Sin BD configurada/inalcanzable -> lista vacia
        }
    }

    /// <summary>
    /// POST /api/v2/admin/sync-matutino
    /// Ejecuta la sincronizacion matutina reutilizando RouteService
    /// (misma logica que /api/v1/routes/sync, sin requerir JWT de vendedor).
    /// </summary>
    [HttpPost("sync-matutino")]
    public async Task<IActionResult> EjecutarSyncMatutino([FromBody] SyncMatutinoRequest request)
    {
        if (request?.VendedorId == null || request.VendedorId <= 0)
            return BadRequest(new { message = "Indica un vendedor_id valido." });

        var sesion = new UsuarioSesion
        {
            VendedorId = request.VendedorId.Value,
            VendedorNombre = "ADMIN",
            CajeroId = _configuration.GetValue<int>("MicrosipSettings:DefaultCajeroId", 2419),
            CajaId = 0,
            AlmacenId = _configuration.GetValue<int>("MicrosipSettings:DefaultAlmacenId", 19),
            SucursalId = _configuration.GetValue<int>("MicrosipSettings:DefaultSucursalId", 4274),
            Usuario = "admin"
        };

        try
        {
            var response = await _routeService.ObtenerSyncMatutinoAsync(sesion);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en sync matutino admin (vendedor {VendedorId})", request.VendedorId);
            return StatusCode(500, new
            {
                message = "Error al ejecutar la sincronizacion matutina.",
                detalle = ex.Message
            });
        }
    }

    public class SyncMatutinoRequest
    {
        public int? VendedorId { get; set; }
    }
}
