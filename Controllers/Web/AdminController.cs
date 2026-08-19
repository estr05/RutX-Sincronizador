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
[Rutx.Sincronizador.Security.AdminAuth]
[ApiController]
[Route("api/v2/admin")]
public class AdminController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IRouteService _routeService;
    private readonly IAuditoriaCompatibilidadService _auditoriaService;
    private readonly ILogger<AdminController> _logger;
    private readonly string _appSettingsPath;
    private readonly string _contentRootPath;

    public AdminController(
        IConfiguration configuration,
        IRouteService routeService,
        IAuditoriaCompatibilidadService auditoriaService,
        IHostEnvironment environment,
        ILogger<AdminController> logger)
    {
        _configuration = configuration;
        _routeService = routeService;
        _auditoriaService = auditoriaService;
        _logger = logger;
        _contentRootPath = environment.ContentRootPath;

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
            
            // Redactar secciones criticas
            var dict = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            dict.Remove("ConnectionStrings");
            dict.Remove("Jwt");
            dict.Remove("WebAuth");

            return Ok(JsonSerializer.SerializeToElement(dict));
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

        // Validacion minima: MicrosipSettings debe venir
        if (!config.TryGetProperty("MicrosipSettings", out var v) || v.ValueKind != JsonValueKind.Object)
            return BadRequest(new { message = "Falta la seccion 'MicrosipSettings' en el JSON." });

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

            // Remover secciones protegidas para que no se puedan sobreescribir desde el panel
            seccionesEntrantes.Remove("ConnectionStrings");
            seccionesEntrantes.Remove("Jwt");
            seccionesEntrantes.Remove("WebAuth");

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

    /// <summary>
    /// POST /api/v2/admin/auditoria
    /// Ejecuta la auditoria de compatibilidad EN VIVO contra la BD configurada
    /// (mismo servicio que usa el wizard de instalacion). 100% solo lectura.
    /// Guarda el resultado en auditoria_resultado.json (raiz del ContentRoot)
    /// para que el panel lo reutilice sin reconectar.
    /// </summary>
    [HttpPost("auditoria")]
    public async Task<IActionResult> EjecutarAuditoria()
    {
        var connectionString = _configuration.GetConnectionString("FirebirdConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return BadRequest(new { message = "No hay cadena de conexion Firebird configurada." });

        try
        {
            var resultado = await _auditoriaService.EjecutarAsync(connectionString);

            try
            {
                var rutaJson = Path.Combine(_contentRootPath, "auditoria_resultado.json");
                var json = JsonSerializer.Serialize(resultado, new JsonSerializerOptions { WriteIndented = true });
                await System.IO.File.WriteAllTextAsync(rutaJson, json);
                _logger.LogInformation("Auditoria guardada en {Ruta}", rutaJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar auditoria_resultado.json");
            }

            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ejecutando la auditoria de compatibilidad");
            return StatusCode(500, new
            {
                message = "Error al ejecutar la auditoria.",
                detalle = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/v2/admin/auditoria/resultado
    /// Devuelve el ultimo resultado de auditoria guardado (JSON en la raiz).
    /// </summary>
    [HttpGet("auditoria/resultado")]
    public IActionResult ObtenerAuditoriaGuardada()
    {
        try
        {
            var rutaJson = Path.Combine(_contentRootPath, "auditoria_resultado.json");
            if (!System.IO.File.Exists(rutaJson))
                return NotFound(new { message = "No hay auditoria guardada todavia. Ejecuta POST /api/v2/admin/auditoria." });

            return Content(System.IO.File.ReadAllText(rutaJson), "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leyendo auditoria_resultado.json");
            return StatusCode(500, new { message = "No se pudo leer la auditoria guardada: " + ex.Message });
        }
    }

    /// <summary>
    /// GET /api/v2/admin/conexion
    /// Devuelve la cadena Firebird configurada DESGLOSADA en sus componentes
    /// (ruta BD, usuario, password, servidor, puerto, dialecto, charset,
    /// pooling, timeout) para mostrarlos como campos estructurados en el panel.
    /// FbConnectionStringBuilder normaliza los aliases del provider
    /// (initial catalog, user id, port number, character set...), asi que
    /// cualquier formato de cadena se desglosa igual.
    /// </summary>
    [HttpGet("conexion")]
    public IActionResult GetConexion()
    {
        var connectionString = _configuration.GetConnectionString("FirebirdConnection") ?? "";
        if (string.IsNullOrWhiteSpace(connectionString))
            return Ok(new { cadena = "", campos = (object?)null });

        try
        {
            var builder = new FbConnectionStringBuilder(connectionString);
            return Ok(new
            {
                campos = new
                {
                    database = builder.Database,
                    user = builder.UserID,
                    password_configured = !string.IsNullOrEmpty(builder.Password),
                    data_source = builder.DataSource,
                    port = builder.Port,
                    dialect = builder.Dialect,
                    pooling = builder.Pooling,
                    charset = builder.Charset,
                    connection_timeout = builder.ConnectionTimeout
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cadena Firebird no parseable");
            return Ok(new
            {
                campos = (object?)null,
                error = "La cadena no se pudo desglosar: " + ex.Message
            });
        }
    }

    // ========================================================================
    // VALIDACION DE IDs CRITICOS POR CAMPO (analisis de la BD configurada)
    // El panel usa esto para: bloquear los campos correctos, marcar los que
    // fallan y ofrecer la busqueda del ID correcto.
    // ========================================================================

    /// <summary>
    /// GET /api/v2/admin/ids-criticos
    /// Valida en vivo cada ID de MicrosipSettings contra la BD configurada.
    /// Devuelve por campo: tabla/columna, si el ID existe y el nombre real
    /// del registro (ej. "CONTADO"). No modifica nada: 100% solo lectura.
    /// </summary>
    [HttpGet("ids-criticos")]
    public async Task<IActionResult> GetIdsCriticos()
    {
        var connectionString = _configuration.GetConnectionString("FirebirdConnection");
        var bd = string.IsNullOrWhiteSpace(connectionString) ? "" : ExtraerRutaBd(connectionString);

        // Version detectada del header .fdb (sin conexion): funciona para todas
        // las versiones, incluso cuando el servidor local no puede abrir la BD
        // (ej. BD Firebird 5.0 con servidor Firebird 3.0).
        var (odsMajor, odsMinor) = FirebirdVersionDetector.LeerOdsDeArchivo(bd);
        string versionFirebird = odsMajor > 0
            ? FirebirdVersionDetector.NombreFirebirdDeOds(odsMajor, odsMinor)
            : "";
        int? odsMayor = odsMajor > 0 ? odsMajor : null;
        // El minor puede ser legitimo 0 (ej. ODS 12.0, 13.0): solo es invalido si el major no se leyo
        int? odsMenor = odsMayor.HasValue ? odsMinor : null;

        if (string.IsNullOrWhiteSpace(connectionString))
            return Ok(new { conexion_ok = false, bd, version_firebird = versionFirebird, ods_major = odsMayor, ods_minor = odsMenor, error = "No hay cadena de conexion Firebird configurada.", campos = Array.Empty<object>() });

        try
        {
            using var connection = new FbConnection(connectionString);
            await connection.OpenAsync();

            var campos = new List<object>();
            var settings = _configuration.GetSection("MicrosipSettings");

            // (Clave, Tabla, Columna, Id, Descripcion, EsAviso, CatalogoBusqueda)
            var escalares = new (string, string, string, int, string, bool, string)[]
            {
                ("DefaultMonedaId", "MONEDAS", "MONEDA_ID", settings.GetValue("DefaultMonedaId", 1), "Moneda base", false, "monedas"),
                ("DefaultCondPagoId", "CONDICIONES_PAGO", "COND_PAGO_ID", settings.GetValue("DefaultCondPagoId", 1), "Condicion de pago por defecto", false, "condiciones_pago"),
                ("DefaultSucursalId", "SUCURSALES", "SUCURSAL_ID", settings.GetValue("DefaultSucursalId", 4274), "Sucursal por defecto", false, "sucursales"),
                ("DefaultAlmacenId", "ALMACENES", "ALMACEN_ID", settings.GetValue("DefaultAlmacenId", 19), "Almacen por defecto", false, "almacenes"),
                ("DefaultImpuestoId", "IMPUESTOS", "IMPUESTO_ID", settings.GetValue("DefaultImpuestoId", 622), "Impuesto por defecto", false, "impuestos"),
                ("DefaultPrecioEmpresaId", "PRECIOS_EMPRESA", "PRECIO_EMPRESA_ID", settings.GetValue("DefaultPrecioEmpresaId", 42), "Precio empresa (lista de precios)", false, "precios_empresa"),
                ("DefaultFormaCobroId", "FORMAS_COBRO", "FORMA_COBRO_ID", settings.GetValue("DefaultFormaCobroId", 67), "Forma de cobro contado", false, "formas_cobro"),
                ("DefaultCajeroId", "CAJEROS", "CAJERO_ID", settings.GetValue("DefaultCajeroId", 2419), "Cajero fallback (si no hay cajero por USUARIO)", true, "cajeros"),
                ("DefaultConceptoCobroId", "CONCEPTOS_CC", "CONCEPTO_CC_ID", settings.GetValue("DefaultConceptoCobroId", 11), "Concepto de cobro (abono CxC)", false, "conceptos_cc"),
            };

            foreach (var (clave, tabla, columna, id, desc, aviso, catalogo) in escalares)
                campos.Add(await ValidarIdAsync(connection, clave, tabla, columna, id, desc, aviso, catalogo, null));

            var creditIds = settings.GetSection("CreditFormaCobroIds").Get<int[]>() ?? Array.Empty<int>();
            for (int i = 0; i < creditIds.Length; i++)
                campos.Add(await ValidarIdAsync(connection, "CreditFormaCobroIds", "FORMAS_COBRO", "FORMA_COBRO_ID",
                    creditIds[i], $"Forma de cobro a credito [{i + 1}]", true, "formas_cobro", i));

            return Ok(new { conexion_ok = true, bd, version_firebird = versionFirebird, ods_major = odsMayor, ods_minor = odsMenor, campos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validando IDs criticos");
            return Ok(new { conexion_ok = false, bd, version_firebird = versionFirebird, ods_major = odsMayor, ods_minor = odsMenor, error = ex.Message, campos = Array.Empty<object>() });
        }
    }

    /// <summary>
    /// GET /api/v2/admin/catalogos/{catalogo}?q=...
    /// Consultas PREDEFINIDAS para buscar el ID correcto de un catalogo
    /// (MONEDAS, CONDICIONES_PAGO, SUCURSALES, ALMACENES, IMPUESTOS,
    /// FORMAS_COBRO, CAJEROS, CONCEPTOS_CC, PRECIOS_EMPRESA).
    /// Busca por nombre (LIKE) o por ID exacto. La columna de nombre se
    /// descubre de los metadatos porque cada BD Microsip la nombra distinto
    /// (DESCRIPCION vs NOMBRE).
    /// </summary>
    [HttpGet("catalogos/{catalogo}")]
    public async Task<IActionResult> BuscarCatalogo(string catalogo, string? q = null, int limite = 50)
    {
        var connectionString = _configuration.GetConnectionString("FirebirdConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            return BadRequest(new { message = "No hay cadena de conexion Firebird configurada." });

        if (!Catalogos.TryGetValue(catalogo.ToLowerInvariant(), out var spec))
            return NotFound(new { message = $"Catalogo desconocido '{catalogo}'. Validos: {string.Join(", ", Catalogos.Keys)}" });

        limite = Math.Clamp(limite, 1, 200);

        try
        {
            using var connection = new FbConnection(connectionString);
            await connection.OpenAsync();

            var nombreCol = await DescubrirColumnaNombreAsync(connection, spec.Tabla, spec.IdColumna);

            // Columnas extra que si existan en esta BD (TIPO, USUARIO, PCTJE...)
            var extras = new List<string>();
            foreach (var extra in spec.Extra)
                if (await ExisteColumnaAsync(connection, spec.Tabla, extra))
                    extras.Add(extra);

            var qTrim = q?.Trim() ?? "";
            if (!string.IsNullOrEmpty(qTrim) && string.IsNullOrEmpty(nombreCol) && !int.TryParse(qTrim, out _))
                return Ok(Array.Empty<object>()); // sin columna de nombre no hay busqueda textual

            var seleccion = spec.IdColumna;
            if (!string.IsNullOrEmpty(nombreCol))
                seleccion += $", TRIM({nombreCol}) AS NOMBRE_ACTUAL";
            foreach (var extra in extras)
                seleccion += $", TRIM({extra}) AS {extra}";

            string where = "";
            if (!string.IsNullOrEmpty(qTrim))
            {
                if (int.TryParse(qTrim, out _))
                    where = $"WHERE {spec.IdColumna} = @Q OR UPPER(TRIM({NombreSql(nombreCol)})) LIKE UPPER(@Pat)";
                else
                    where = $"WHERE UPPER(TRIM({NombreSql(nombreCol)})) LIKE UPPER(@Pat)";
            }

            var sql = $"SELECT FIRST @Limite {seleccion} FROM {spec.Tabla} {where} ORDER BY {spec.IdColumna}";
            var rows = await connection.QueryAsync(sql,
                new { Q = int.TryParse(qTrim, out var n) ? n : -1, Pat = "%" + qTrim + "%", Limite = limite });

            var resultado = new List<object>();
            foreach (var row in rows)
            {
                var dict = (IDictionary<string, object?>)row;
                resultado.Add(new
                {
                    id = Convert.ToInt32(dict[spec.IdColumna]),
                    nombre = dict.TryGetValue("NOMBRE_ACTUAL", out var nm) ? nm?.ToString() ?? "" : "",
                    extra = string.Join(" · ", extras
                        .Select(e => dict.TryGetValue(e, out var v) ? (v?.ToString() ?? "").Trim() : "")
                        .Where(s => s.Length > 0))
                });
            }

            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error buscando en catalogo {Catalogo}", catalogo);
            return StatusCode(500, new { message = "No se pudo consultar el catalogo: " + ex.Message });
        }
    }

    /// <summary>
    /// Catalogos disponibles para la busqueda predefinida.
    /// Tabla, columna ID y columnas informativas opcionales.
    /// </summary>
    private static readonly Dictionary<string, (string Tabla, string IdColumna, string[] Extra)> Catalogos = new()
    {
        ["monedas"] = ("MONEDAS", "MONEDA_ID", Array.Empty<string>()),
        ["condiciones_pago"] = ("CONDICIONES_PAGO", "COND_PAGO_ID", Array.Empty<string>()),
        ["sucursales"] = ("SUCURSALES", "SUCURSAL_ID", Array.Empty<string>()),
        ["almacenes"] = ("ALMACENES", "ALMACEN_ID", Array.Empty<string>()),
        ["impuestos"] = ("IMPUESTOS", "IMPUESTO_ID", new[] { "PCTJE_IMPUESTO" }),
        ["formas_cobro"] = ("FORMAS_COBRO", "FORMA_COBRO_ID", new[] { "TIPO" }),
        ["cajeros"] = ("CAJEROS", "CAJERO_ID", new[] { "USUARIO" }),
        ["conceptos_cc"] = ("CONCEPTOS_CC", "CONCEPTO_CC_ID", Array.Empty<string>()),
        ["precios_empresa"] = ("PRECIOS_EMPRESA", "PRECIO_EMPRESA_ID", Array.Empty<string>()),
    };

    private async Task<object> ValidarIdAsync(
        FbConnection connection, string clave, string tabla, string columna, int id,
        string descripcion, bool esAviso, string catalogo, int? posicion)
    {
        try
        {
            var existe = await connection.ExecuteScalarAsync<int>(
                $"SELECT COUNT(*) FROM {tabla} WHERE {columna} = @Id", new { Id = id }) > 0;

            string? nombre = null;
            if (existe)
            {
                var nombreCol = await DescubrirColumnaNombreAsync(connection, tabla, columna);
                if (!string.IsNullOrEmpty(nombreCol))
                {
                    nombre = await connection.ExecuteScalarAsync<string?>(
                        $"SELECT TRIM({nombreCol}) FROM {tabla} WHERE {columna} = @Id", new { Id = id });
                    nombre = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim();
                }
            }

            return new
            {
                clave, descripcion, tabla, columna, id_actual = id,
                valido = existe, es_aviso = esAviso, catalogo, posicion,
                nombre_actual = nombre
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validando {Tabla}.{Columna}={Id}", tabla, columna, id);
            return new
            {
                clave, descripcion, tabla, columna, id_actual = id,
                valido = false, es_aviso = esAviso, catalogo, posicion,
                nombre_actual = (string?)null, error = ex.Message
            };
        }
    }

    /// <summary>
    /// Descubre la columna de "nombre" de una tabla: prueba DESCRIPCION/NOMBRE
    /// y si no existen toma la primera columna CHAR/VARCHAR que no sea el ID.
    /// Cada BD Microsip nombra la columna distinto, por eso se hace dinamico.
    /// </summary>
    private static async Task<string> DescubrirColumnaNombreAsync(FbConnection connection, string tabla, string columnaId)
    {
        try
        {
            var columnas = (await connection.QueryAsync<string>(@"
                SELECT TRIM(rf.RDB$FIELD_NAME)
                FROM RDB$RELATION_FIELDS rf
                JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                WHERE TRIM(rf.RDB$RELATION_NAME) = @Tabla
                  AND f.RDB$FIELD_TYPE IN (37, 14)
                  AND TRIM(rf.RDB$FIELD_NAME) <> @ColumnaId
                ORDER BY rf.RDB$FIELD_POSITION", new { Tabla = tabla, ColumnaId = columnaId }))
                .Select(c => (c ?? "").Trim())
                .Where(c => c.Length > 0)
                .ToList();

            foreach (var candidata in new[] { "DESCRIPCION", "NOMBRE" })
            {
                var encontrada = columnas.FirstOrDefault(c => c.Equals(candidata, StringComparison.OrdinalIgnoreCase));
                if (encontrada != null && EsNombreSeguro(encontrada))
                    return encontrada;
            }
            return columnas.FirstOrDefault(EsNombreSeguro) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Solo permite identificadores seguros para interpolar en SQL.
    /// Las columnas/tablas vienen de diccionarios fijos o de metadatos
    /// (RDB$), pero se valida por si acaso (misma defensa que FkResolverService).
    /// </summary>
    private static bool EsNombreSeguro(string nombre) =>
        nombre.Length > 0 && nombre.Length <= 128 && nombre.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static async Task<bool> ExisteColumnaAsync(FbConnection connection, string tabla, string columna)
    {
        try
        {
            var n = await connection.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM RDB$RELATION_FIELDS
                WHERE TRIM(RDB$RELATION_NAME) = @Tabla AND TRIM(RDB$FIELD_NAME) = @Columna",
                new { Tabla = tabla, Columna = columna });
            return n > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NombreSql(string nombreCol) => string.IsNullOrEmpty(nombreCol) ? "NULL" : nombreCol;

    private static string ExtraerRutaBd(string connectionString)
    {
        // Reutiliza el builder: normaliza aliases (initial catalog, comillas, etc.)
        try
        {
            return new FbConnectionStringBuilder(connectionString).Database;
        }
        catch
        {
            // Fallback manual por si la cadena no es valida
            foreach (var parte in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = parte.IndexOf('=');
                if (idx <= 0) continue;
                if (parte[..idx].Trim().Equals("Database", StringComparison.OrdinalIgnoreCase))
                    return parte[(idx + 1)..].Trim().Trim('"');
            }
            return connectionString;
        }
    }

    public class SyncMatutinoRequest
    {
        public int? VendedorId { get; set; }
    }
}
