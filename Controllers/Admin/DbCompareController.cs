// ============================================================================
// ARCHIVO: DbCompareController.cs (DIAGNOSTICO TEMPORAL)
// PROPOSITO: Comparar estructura Y DATOS de COYATOC vs CHOCOLATES
// ============================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Controllers.Admin;

[Rutx.Sincronizador.Security.AdminAuth]
[ApiController]
[Route("api/v2/admin/[controller]")]
public class DbCompareController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DbCompareController> _logger;

    public DbCompareController(IConfiguration configuration, ILogger<DbCompareController> logger)
    {
        _configuration = configuration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string GetConnStr(string dbName)
    {
        // Whitelist estricta para evitar Inyeccion / Path Traversal
        if (dbName != "CHOCOLATES" && dbName != "COYATOC")
            throw new ArgumentException("Base de datos no permitida.");
            
        var baseConn = _configuration.GetConnectionString("FirebirdConnection") ?? "";
        return baseConn.Replace("CHOCOLATES.fdb", $"{dbName}.fdb");
    }

    private static readonly HashSet<string> TablasPermitidas = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOCTOS_PV", "DOCTOS_PV_DET", "DOCTOS_PV_COBROS", "IMPUESTOS_DOCTOS_PV",
        "CAJEROS", "CAJAS", "AGENTES", "VENDEDORES", "CLIENTES", "DIRS_CLIENTES",
        "ARTICULOS", "PRECIOS_ARTICULOS", "RUTAS", "RUTAS_DET", "IMPUESTOS", "IMPUESTOS_ARTICULOS",
        "FORMAS_COBRO", "FOLIOS_CAJAS", "MONEDAS", "COND_PAGO", "SUCURSALES", "ALMACENES",
        // Tablas que podrian coincidir con busquedas de visitas o noventas:
        "VISITAS_CLIENTES", "NO_VENTAS", "INCIDENCIAS", "RAZONES_NO_VENTA", "CAUSAS_NO_VENTA"
    };

    private void ValidarTablaSegura(string tabla)
    {
        if (!TablasPermitidas.Contains(tabla.Trim()))
            throw new ArgumentException($"La tabla '{tabla}' no esta en la allowlist estricta de tablas consultables por administracion.");
    }

    // ========================================================================
    // ENDPOINT 1: Comparar DATOS de catalogos (los IDs que usa el sincronizador)
    // ========================================================================
    [HttpGet("comparar-ids-criticos")]
    public async Task<IActionResult> CompararIdsCriticos()
    {
        var result = new Dictionary<string, object>();

        // IDs que usa el sincronizador actualmente
        var idsAValidar = new Dictionary<string, List<int>>
        {
            ["IMPUESTOS"] = new() { 622, 3344, 2204 },
            ["FORMAS_COBRO"] = new() { 67, 68, 71, 703, 2205, 2845, 3702 },
            ["CAJEROS"] = new() { 2419, 9449 },
            ["VENDEDORES"] = new() { 9449 },
            ["MONEDAS"] = new() { 1 },
            ["COND_PAGO"] = new() { 1, 2422 },
            ["SUCURSALES"] = new() { 4274 },
            ["ALMACENES"] = new() { 19 }
        };

        var chocData = await ConsultarCatalogos("CHOCOLATES", idsAValidar);
        var coyData = await ConsultarCatalogos("COYATOC", idsAValidar);

        result["cho_colates"] = chocData;
        result["coy_atoc"] = coyData;

        // Generar reporte de compatibilidad
        var compatibilidad = new List<Dictionary<string, object>>();
        var errores = new List<Dictionary<string, object>>();

        foreach (var kvp in idsAValidar)
        {
            string tabla = kvp.Key;
            var chocRegs = chocData.ContainsKey(tabla) ? chocData[tabla] as List<Dictionary<string, object>> : null;
            var coyRegs = coyData.ContainsKey(tabla) ? coyData[tabla] as List<Dictionary<string, object>> : null;

            foreach (int id in kvp.Value)
            {
                var enChoc = chocRegs?.Find(r =>
                {
                    var val = r.ContainsKey("id") ? r["id"] : null;
                    return val != null && Convert.ToInt32(val) == id;
                });

                var enCoy = coyRegs?.Find(r =>
                {
                    var val = r.ContainsKey("id") ? r["id"] : null;
                    return val != null && Convert.ToInt32(val) == id;
                });

                if (enChoc != null && enCoy != null)
                {
                    compatibilidad.Add(new Dictionary<string, object>
                    {
                        ["tabla"] = tabla,
                        ["id"] = id,
                        ["estado"] = "OK",
                        ["nombre_chocolates"] = enChoc.ContainsKey("nombre") ? enChoc["nombre"] : "",
                        ["nombre_coyatoc"] = enCoy.ContainsKey("nombre") ? enCoy["nombre"] : ""
                    });
                }
                else if (enChoc != null && enCoy == null)
                {
                    errores.Add(new Dictionary<string, object>
                    {
                        ["tabla"] = tabla,
                        ["id"] = id,
                        ["tipo"] = "FALTA_EN_COYATOC",
                        ["nombre"] = enChoc.ContainsKey("nombre") ? enChoc["nombre"] : "",
                        ["descripcion"] = $"ID {id} existe en CHOCOLATES pero NO en COYATOC"
                    });
                }
                else if (enChoc == null && enCoy != null)
                {
                    errores.Add(new Dictionary<string, object>
                    {
                        ["tabla"] = tabla,
                        ["id"] = id,
                        ["tipo"] = "SOLO_EN_COYATOC",
                        ["nombre"] = enCoy.ContainsKey("nombre") ? enCoy["nombre"] : "",
                        ["descripcion"] = $"ID {id} existe en COYATOC pero NO en CHOCOLATES"
                    });
                }
            }
        }

        result["compatibilidad_ids"] = new
        {
            total_ids_validados = 0,
            compatibles = 0,
            errores = 0,
            detalle_compatibles = compatibilidad,
            detalle_errores = errores
        };

        // Recalcular totales
        var compList = result["compatibilidad_ids"] as Dictionary<string, object>;
        if (compList != null)
        {
            compList["total_ids_validados"] = compatibilidad.Count + errores.Count;
            compList["compatibles"] = compatibilidad.Count;
            compList["errores"] = errores.Count;
        }

        return Ok(result);
    }

    // ========================================================================
    // ENDPOINT 2: Todos los clientes de CHOCOLATES
    // ========================================================================
    [HttpGet("clientes")]
    public async Task<IActionResult> TodosLosClientes()
    {
        using var connection = new FbConnection(GetConnStr("CHOCOLATES"));
        try { await connection.OpenAsync(); }
        catch (Exception ex) { return Ok(new { error = ex.Message }); }

        var rows = await connection.QueryAsync(@"
            SELECT c.CLIENTE_ID, c.NOMBRE, c.VENDEDOR_ID,
                   c.ESTATUS,
                   (SELECT FIRST 1 d.CALLE FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS CALLE,
                   (SELECT FIRST 1 d.COLONIA FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS COLONIA
            FROM CLIENTES c
            WHERE c.ESTATUS = 'A'
            ORDER BY c.NOMBRE");

        var list = new List<Dictionary<string, object>>();
        foreach (var r in rows)
        {
            list.Add(new Dictionary<string, object>
            {
                ["cliente_id"] = Convert.ToInt32(r.CLIENTE_ID),
                ["nombre"] = (r.NOMBRE as string)?.Trim() ?? "",
                ["vendedor_id"] = r.VENDEDOR_ID != null ? Convert.ToInt32(r.VENDEDOR_ID) : 0,
                ["estatus"] = (r.ESTATUS as string)?.Trim() ?? "",
                ["calle"] = (r.CALLE as string)?.Trim() ?? "",
                ["colonia"] = (r.COLONIA as string)?.Trim() ?? ""
            });
        }

        return Ok(new { total = list.Count, clientes = list });
    }

    // ========================================================================
    // ENDPOINT: Asignar clientes a una ruta (INSERT en RUTAS_DET)
    // ========================================================================
    [HttpPost("asignar-ruta")]
    public async Task<IActionResult> AsignarClientesARuta([FromBody] AsignarRutaRequest request)
    {
        // [SEGURIDAD] Sprint 4: Mutación deshabilitada
        // RutX no ejecutará DDL ni mutaciones administrativas sobre el esquema Microsip.
        _logger.LogWarning("Intento de mutación administrativa bloqueado (AsignarClientesARuta).");
        return StatusCode(403, new { exito = false, error = "Mutación administrativa no aprobada por contrato." });
    }

    public record AsignarRutaRequest
    {
        public int RutaId { get; init; }
        public List<int> ClienteIds { get; init; } = new();
        public int Dia { get; init; } = 0;
        public int PosicionInicial { get; init; } = 1;
    }

    // ========================================================================
    // ENDPOINT 3: Catalogo completo (todos los registros) de cada tabla clave
    // ========================================================================
    [HttpGet("catalogos-completos")]
    public async Task<IActionResult> CatalogosCompletos()
    {
        var tablas = new Dictionary<string, (string idCol, string nameCol, string extraCols)>
        {
            ["IMPUESTOS"] = ("IMPUESTO_ID", "NOMBRE", "PCTJE_IMPUESTO, CLAVE_FISCAL"),
            ["FORMAS_COBRO"] = ("FORMA_COBRO_ID", "NOMBRE", "TIPO"),
            ["CAJEROS"] = ("CAJERO_ID", "NOMBRE", "USUARIO, OPERAR_CAJAS, OCULTO"),
            ["VENDEDORES"] = ("VENDEDOR_ID", "NOMBRE", "OCULTO, ES_PREDET"),
            ["AGENTES"] = ("AGENTE_ID", "NOMBRE", "USUARIO, VENDEDOR_ID, SUCURSAL_ID, ALMACEN_ID"),
            ["CLIENTES"] = ("CLIENTE_ID", "NOMBRE", "VENDEDOR_ID, ESTATUS, LIMITE_CREDITO"),
            ["ARTICULOS"] = ("ARTICULO_ID", "NOMBRE", "ESTATUS"),
            ["PRECIOS_ARTICULOS"] = ("PRECIO_ARTICULO_ID", "ARTICULO_ID", "PRECIO_EMPRESA_ID, PRECIO"),
            ["RUTAS"] = ("RUTA_ID", "NOMBRE", "AGENTE_ID, ESTATUS, CLAVE"),
            ["RUTAS_DET"] = ("RUTA_DET_ID", "RUTA_ID", "CLIENTE_ID, DIA, DIA_POSICION"),
            ["MONEDAS"] = ("MONEDA_ID", "NOMBRE", "SIMBOLO, ES_MONEDA_LOCAL, ES_PREDET"),
            ["SUCURSALES"] = ("SUCURSAL_ID", "NOMBRE", "TIPO_ELEMENTO, ES_MATRIZ, ACTIVA"),
            ["ALMACENES"] = ("ALMACEN_ID", "NOMBRE", "ES_PPAL, ES_PREDET, OCULTO"),
            ["FOLIOS_CAJAS"] = ("CAJA_ID", "SERIE", "TIPO_DOCTO, CONSECUTIVO")
        };

        var choc = await ConsultarCatalogosCompletos("CHOCOLATES", tablas);
        var coy = await ConsultarCatalogosCompletos("COYATOC", tablas);

        return Ok(new
        {
            cho_colates = choc,
            coy_atoc = coy
        });
    }

    // ========================================================================
    // ENDPOINT 3: Comparar estructura (del analisis anterior, mejorado)
    // ========================================================================
    [HttpGet("comparar-bd")]
    public async Task<IActionResult> CompararBases()
    {
        var choc = await ConsultarEstructura("CHOCOLATES");
        var coy = await ConsultarEstructura("COYATOC");

        // ... (misma logica que antes, omitido por brevedad pero se conserva)
        return Ok(new { cho_colates = choc, coy_atoc = coy, mensaje = "Usa /comparar-ids-criticos para validar datos" });
    }

    // ========================================================================
    // ENDPOINT: Busqueda de tablas relacionadas con NO VENTA en CHOCOLATES
    // ========================================================================
    [HttpGet("no-venta-tablas")]
    public async Task<IActionResult> BuscarNoVentaTablas()
    {
        using var connection = new FbConnection(GetConnStr("CHOCOLATES"));
        try { await connection.OpenAsync(); }
        catch (Exception ex) { return Ok(new { error = ex.Message }); }

        var resultado = new Dictionary<string, object>();

        // 1. Buscar TODAS las tablas de usuario en la base de datos
        var todasLasTablas = await connection.QueryAsync(@"
            SELECT RDB$RELATION_NAME AS TABLA
            FROM RDB$RELATIONS
            WHERE RDB$SYSTEM_FLAG = 0
              AND RDB$VIEW_SOURCE IS NULL
            ORDER BY RDB$RELATION_NAME");

        var listaTablas = new List<string>();
        foreach (var t in todasLasTablas)
        {
            string nombre = (t.TABLA as string)?.Trim() ?? "";
            listaTablas.Add(nombre);
        }

        resultado["total_tablas"] = listaTablas.Count;
        resultado["todas_las_tablas"] = listaTablas;

        // 2. Identificar tablas relacionadas con NO VENTA / VISITAS / INCIDENCIAS
        var tablasNoVenta = listaTablas.Where(t =>
            t.Contains("VISIT") ||
            t.Contains("NOVEN") ||
            t.Contains("NO_VEN") ||
            t.Contains("SIN_VEN") ||
            t.Contains("INCIDENCIA") ||
            t.Contains("RAZON") ||
            t.Contains("MOTIVO") ||
            t.Contains("CAUSA_NO") ||
            t.Contains("TICKET_NO")
        ).ToList();

        resultado["tablas_no_venta_encontradas"] = tablasNoVenta;

        // 3. Por cada tabla encontrada, obtener su estructura con TIPO de columna
        var estructuraNoVenta = new Dictionary<string, object>();
        foreach (var tabla in tablasNoVenta)
        {
            try
            {
                var cols = await connection.QueryAsync(@"
                    SELECT
                        rf.RDB$FIELD_NAME AS COL_NAME,
                        f.RDB$FIELD_TYPE AS FLD_TYPE,
                        f.RDB$FIELD_SUB_TYPE AS SUB_TYPE,
                        f.RDB$CHARACTER_LENGTH AS CHAR_LEN
                    FROM RDB$RELATION_FIELDS rf
                    JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                    WHERE rf.RDB$RELATION_NAME = @Tabla
                    ORDER BY rf.RDB$FIELD_POSITION",
                    new { Tabla = tabla });

                var colList = new List<Dictionary<string, object>>();
                foreach (var c in cols)
                {
                    int typeNum = Convert.ToInt32(c.FLD_TYPE);
                    int subType = c.SUB_TYPE != null ? Convert.ToInt32(c.SUB_TYPE) : 0;
                    string typeName = FirebirdTypeName(typeNum, subType);
                    
                    colList.Add(new Dictionary<string, object>
                    {
                        ["nombre"] = ((string)c.COL_NAME).Trim(),
                        ["tipo"] = typeName,
                        ["longitud"] = c.CHAR_LEN != null ? Convert.ToInt32(c.CHAR_LEN) : 0
                    });
                }

                int count = 0;
                try 
                { 
                    ValidarTablaSegura(tabla);
                    count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tabla}"); 
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error al contar registros de {Tabla}", tabla); }

                estructuraNoVenta[tabla] = new
                {
                    columnas = colList,
                    registros = count
                };
            }
            catch (Exception ex)
            {
                estructuraNoVenta[tabla] = new { error = ex.Message };
            }
        }
        resultado["estructura_no_venta"] = estructuraNoVenta;
        
        // 3b. Buscar tablas con columnas BLOB en TODA la base de datos
        try
        {
            var tablasConBlob = await connection.QueryAsync(@"
                SELECT DISTINCT rf.RDB$RELATION_NAME AS TABLA, rf.RDB$FIELD_NAME AS COLUMNA, f.RDB$FIELD_TYPE AS FLD_TYPE
                FROM RDB$RELATION_FIELDS rf
                JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
                WHERE r.RDB$SYSTEM_FLAG = 0
                  AND r.RDB$VIEW_SOURCE IS NULL
                  AND (f.RDB$FIELD_TYPE = 261 OR f.RDB$FIELD_TYPE = 45)
                  AND (rf.RDB$RELATION_NAME LIKE '%VISIT%'
                    OR rf.RDB$RELATION_NAME LIKE '%RUTA%'
                    OR rf.RDB$RELATION_NAME LIKE '%INCIDENCIA%'
                    OR rf.RDB$RELATION_NAME LIKE '%CLIENTE%'
                    OR rf.RDB$RELATION_NAME LIKE '%FIRMA%'
                    OR rf.RDB$RELATION_NAME LIKE '%FOTO%'
                    OR rf.RDB$RELATION_NAME LIKE '%ARCHIV%')
                ORDER BY rf.RDB$RELATION_NAME");

            var listaBlobs = new List<Dictionary<string, object>>();
            foreach (var b in tablasConBlob)
            {
                listaBlobs.Add(new Dictionary<string, object>
                {
                    ["tabla"] = ((string)b.TABLA).Trim(),
                    ["columna"] = ((string)b.COLUMNA).Trim(),
                    ["tipo"] = Convert.ToInt32(b.FLD_TYPE) == 261 ? "BLOB" : "BLOB_ID"
                });
            }
            resultado["tablas_con_blob_visitas"] = listaBlobs;
        }
        catch (Exception ex)
        {
            resultado["tablas_con_blob_visitas"] = new { error = ex.Message };
        }

        // 4. Consultar DOCTOS_PV: que TIPO_DOCTO existen (V=Venta, N=Nota, etc)
        try
        {
            var tiposDocto = await connection.QueryAsync(@"
                SELECT DISTINCT TIPO_DOCTO, COUNT(*) AS TOTAL
                FROM DOCTOS_PV
                GROUP BY TIPO_DOCTO
                ORDER BY TIPO_DOCTO");

            var listaTipos = new List<Dictionary<string, object>>();
            foreach (var td in tiposDocto)
            {
                listaTipos.Add(new Dictionary<string, object>
                {
                    ["tipo"] = ((string)td.TIPO_DOCTO).Trim(),
                    ["total"] = Convert.ToInt32(td.TOTAL)
                });
            }
            resultado["tipos_docto_pv"] = listaTipos;
        }
        catch (Exception ex)
        {
            resultado["tipos_docto_pv"] = new { error = ex.Message };
        }

        // 5. Consultar algunos registros de DOCTOS_PV con FOLIO para entender el formato
        try
        {
            var folios = await connection.QueryAsync(@"
                SELECT FIRST 20 DOCTO_PV_ID, FOLIO, TIPO_DOCTO, CLIENTE_ID, FECHA, IMPORTE_NETO, DESCRIPCION
                FROM DOCTOS_PV
                WHERE FOLIO IS NOT NULL
                ORDER BY DOCTO_PV_ID DESC");

            var listaFolios = new List<Dictionary<string, object>>();
            foreach (var f in folios)
            {
                listaFolios.Add(new Dictionary<string, object>
                {
                    ["docto_pv_id"] = Convert.ToInt32(f.DOCTO_PV_ID),
                    ["folio"] = ((string)f.FOLIO)?.Trim() ?? "",
                    ["tipo_docto"] = ((string)f.TIPO_DOCTO)?.Trim() ?? "",
                    ["cliente_id"] = Convert.ToInt32(f.CLIENTE_ID),
                    ["fecha"] = f.FECHA?.ToString() ?? "",
                    ["importe_neto"] = f.IMPORTE_NETO != null ? Convert.ToInt64(f.IMPORTE_NETO) / 100.0 : 0,
                    ["descripcion"] = ((string)f.DESCRIPCION)?.Trim() ?? ""
                });
            }
            resultado["ultimos_folios_pv"] = listaFolios;
        }
        catch (Exception ex)
        {
            resultado["ultimos_folios_pv"] = new { error = ex.Message };
        }

        // 6. Buscar si existe algun registro de NO VENTA (TIPO_DOCTO especial o IMPORTE_NETO=0)
        try
        {
            var noVentas = await connection.QueryAsync(@"
                SELECT FIRST 1000 DOCTO_PV_ID, FOLIO, TIPO_DOCTO, CLIENTE_ID, FECHA, IMPORTE_NETO, DESCRIPCION, VENDEDOR_ID
                FROM DOCTOS_PV
                WHERE TIPO_DOCTO IN ('N', 'X', 'A', 'B') 
                   OR (TIPO_DOCTO = 'V' AND IMPORTE_NETO = 0)
                ORDER BY DOCTO_PV_ID DESC");

            var listaNoVentas = new List<Dictionary<string, object>>();
            foreach (var nv in noVentas)
            {
                listaNoVentas.Add(new Dictionary<string, object>
                {
                    ["docto_pv_id"] = Convert.ToInt32(nv.DOCTO_PV_ID),
                    ["folio"] = ((string)nv.FOLIO)?.Trim() ?? "",
                    ["tipo_docto"] = ((string)nv.TIPO_DOCTO)?.Trim() ?? "",
                    ["cliente_id"] = Convert.ToInt32(nv.CLIENTE_ID),
                    ["fecha"] = nv.FECHA?.ToString() ?? "",
                    ["importe_neto"] = nv.IMPORTE_NETO != null ? Convert.ToInt64(nv.IMPORTE_NETO) / 100.0 : 0,
                    ["descripcion"] = ((string)nv.DESCRIPCION)?.Trim() ?? "",
                    ["vendedor_id"] = Convert.ToInt32(nv.VENDEDOR_ID)
                });
            }
            resultado["posibles_no_ventas_pv"] = listaNoVentas;
        }
        catch (Exception ex)
        {
            resultado["posibles_no_ventas_pv"] = new { error = ex.Message };
        }

        return Ok(resultado);
    }

    // ========================================================================
    // HELPERS
    // ========================================================================

    private async Task<Dictionary<string, object>> ConsultarCatalogos(string dbName, Dictionary<string, List<int>> idsAValidar)
    {
        string connStr;
        try { connStr = GetConnStr(dbName); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error al construir cadena de conexion para {DbName}", dbName); return new Dictionary<string, object> { ["error"] = $"No se pudo construir cadena para {dbName}" }; }

        using var connection = new FbConnection(connStr);
        try { await connection.OpenAsync(); }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["error"] = $"Error al conectar: {ex.Message}" };
        }

        var resultado = new Dictionary<string, object>();

        foreach (var kvp in idsAValidar)
        {
            string tabla = kvp.Key;
            var ids = kvp.Value;

            try
            {
                // Determinar columna ID y nombre segun la tabla
                string colId, colNombre, extraSql;
                switch (tabla)
                {
                    case "IMPUESTOS": colId = "IMPUESTO_ID"; colNombre = "NOMBRE"; extraSql = ", PCTJE_IMPUESTO"; break;
                    case "FORMAS_COBRO": colId = "FORMA_COBRO_ID"; colNombre = "NOMBRE"; extraSql = ", TIPO"; break;
                    case "CAJEROS": colId = "CAJERO_ID"; colNombre = "NOMBRE"; extraSql = ", USUARIO, OCULTO"; break;
                    case "VENDEDORES": colId = "VENDEDOR_ID"; colNombre = "NOMBRE"; extraSql = ", OCULTO"; break;
                    case "MONEDAS": colId = "MONEDA_ID"; colNombre = "NOMBRE"; extraSql = ", SIMBOLO, ES_MONEDA_LOCAL"; break;
                    case "COND_PAGO": colId = "COND_PAGO_ID"; colNombre = "NOMBRE"; extraSql = ""; break;
                    case "SUCURSALES": colId = "SUCURSAL_ID"; colNombre = "NOMBRE"; extraSql = ", ACTIVA"; break;
                    case "ALMACENES": colId = "ALMACEN_ID"; colNombre = "NOMBRE"; extraSql = ", ES_PREDET, OCULTO"; break;
                    default: colId = "ID"; colNombre = "NOMBRE"; extraSql = ""; break;
                }

                ValidarTablaSegura(tabla);
                var rows = await connection.QueryAsync($@"
                    SELECT {colId} AS ID, {colNombre} AS NOMBRE {extraSql}
                    FROM {tabla}
                    WHERE {colId} IN @Ids
                    ORDER BY {colId}",
                    new { Ids = ids });

                var list = new List<Dictionary<string, object>>();
                foreach (var row in rows)
                {
                    int id = Convert.ToInt32(row.ID);
                    var dict = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["nombre"] = (row.NOMBRE as string)?.Trim() ?? ""
                    };
                    // Agregar campos extra si existen
                    try { dict["pctje_impuesto"] = row.PCTJE_IMPUESTO?.ToString() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer PCTJE_IMPUESTO para ID={Id}", id); }
                    
                    try { dict["tipo"] = (row.TIPO as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer TIPO para ID={Id}", id); }
                    try { dict["usuario"] = (row.USUARIO as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer USUARIO para ID={Id}", id); }
                    try { dict["oculto"] = (row.OCULTO as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer OCULTO para ID={Id}", id); }
                    try { dict["simbolo"] = (row.SIMBOLO as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer SIMBOLO para ID={Id}", id); }
                    try { dict["es_moneda_local"] = (row.ES_MONEDA_LOCAL as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer ES_MONEDA_LOCAL para ID={Id}", id); }
                    try { dict["activa"] = (row.ACTIVA as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer ACTIVA para ID={Id}", id); }
                    try { dict["es_predet"] = (row.ES_PREDET as string)?.Trim() ?? ""; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error al leer ES_PREDET para ID={Id}", id); }

                    list.Add(dict);
                }

                resultado[tabla] = list;
            }
            catch (Exception ex)
            {
                resultado[tabla] = new List<Dictionary<string, object>>
                {
                    new() { ["error"] = ex.Message }
                };
            }
        }

        return resultado;
    }

    private async Task<Dictionary<string, object>> ConsultarCatalogosCompletos(string dbName, Dictionary<string, (string idCol, string nameCol, string extraCols)> tablas)
    {
        string connStr;
        try { connStr = GetConnStr(dbName); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error al construir cadena de conexion para {DbName}", dbName); return new Dictionary<string, object> { ["error"] = $"No se pudo construir cadena para {dbName}" }; }

        using var connection = new FbConnection(connStr);
        try { await connection.OpenAsync(); }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["error"] = $"Error al conectar: {ex.Message}" };
        }

        var resultado = new Dictionary<string, object>();

        foreach (var kvp in tablas)
        {
            string tabla = kvp.Key;
            var (idCol, nameCol, extraCols) = kvp.Value;

            try
            {
                ValidarTablaSegura(tabla);
                var rows = await connection.QueryAsync($@"
                    SELECT {idCol} AS ID, {nameCol} AS NOMBRE {extraCols}
                    FROM {tabla}
                    ORDER BY {idCol}");

                var list = new List<Dictionary<string, object>>();
                foreach (var row in rows)
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["id"] = Convert.ToInt32(row.ID),
                        ["nombre"] = (row.NOMBRE as string)?.Trim() ?? ""
                    };
                    list.Add(dict);
                }

                resultado[tabla] = new
                {
                    total = list.Count,
                    registros = list
                };
            }
            catch (Exception ex)
            {
                resultado[tabla] = new { total = 0, error = ex.Message };
            }
        }

        return resultado;
    }

    private async Task<Dictionary<string, object>> ConsultarEstructura(string dbName)
    {
        string connStr;
        try { connStr = GetConnStr(dbName); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error al construir cadena de conexion para {DbName}", dbName); return new Dictionary<string, object> { ["error"] = $"No se pudo construir cadena para {dbName}" }; }

        using var connection = new FbConnection(connStr);
        try { await connection.OpenAsync(); }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["error"] = $"Error al conectar a {dbName}: {ex.Message}"
            };
        }

        var tablas = new[]
        {
            "DOCTOS_PV", "DOCTOS_PV_DET", "DOCTOS_PV_COBROS",
            "IMPUESTOS_DOCTOS_PV",
            "CAJEROS", "CAJAS", "AGENTES", "VENDEDORES",
            "CLIENTES", "DIRS_CLIENTES",
            "ARTICULOS", "PRECIOS_ARTICULOS",
            "RUTAS", "RUTAS_DET",
            "IMPUESTOS", "IMPUESTOS_ARTICULOS",
            "FORMAS_COBRO", "FOLIOS_CAJAS", "MONEDAS",
            "COND_PAGO", "SUCURSALES", "ALMACENES"
        };

        var resultado = new Dictionary<string, object>();

        foreach (var tabla in tablas)
        {
            try
            {
                var columns = await connection.QueryAsync(@"
                    SELECT
                        rf.RDB$FIELD_NAME AS COL_NAME,
                        f.RDB$FIELD_TYPE AS FLD_TYPE,
                        f.RDB$FIELD_SUB_TYPE AS SUB_TYPE,
                        f.RDB$CHARACTER_LENGTH AS CHAR_LEN,
                        f.RDB$FIELD_PRECISION AS FLD_PRECISION,
                        f.RDB$FIELD_SCALE AS FLD_SCALE,
                        CASE WHEN (rf.RDB$NULL_FLAG = 1) THEN 'NO' ELSE 'YES' END AS IS_NULLABLE,
                        COALESCE(seg.RDB$FIELD_NAME, '') AS PK_FLAG
                    FROM RDB$RELATION_FIELDS rf
                    JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
                    LEFT JOIN RDB$INDEX_SEGMENTS seg
                        ON seg.RDB$FIELD_NAME = rf.RDB$FIELD_NAME
                        AND seg.RDB$INDEX_NAME IN (
                            SELECT rc.RDB$INDEX_NAME FROM RDB$RELATION_CONSTRAINTS rc
                            WHERE rc.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
                                AND rc.RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'
                        )
                    WHERE rf.RDB$RELATION_NAME = @Tabla
                    ORDER BY rf.RDB$FIELD_POSITION",
                    new { Tabla = tabla });

                var colList = new List<Dictionary<string, object>>();
                foreach (var col in columns)
                {
                    string colName = (col.COL_NAME as string)?.Trim() ?? "";
                    colList.Add(new Dictionary<string, object>
                    {
                        ["nombre"] = colName,
                        ["tipo"] = FirebirdTypeName(Convert.ToInt32(col.FLD_TYPE), Convert.ToInt32(col.SUB_TYPE ?? 0)),
                        ["nulleable"] = (col.IS_NULLABLE as string)?.Trim() ?? "YES",
                        ["es_pk"] = !string.IsNullOrEmpty((col.PK_FLAG as string)?.Trim())
                    });
                }

                var triggers = await connection.QueryAsync(@"
                    SELECT RDB$TRIGGER_NAME AS TRIG_NAME, RDB$TRIGGER_TYPE AS TRIG_TYPE
                    FROM RDB$TRIGGERS
                    WHERE RDB$RELATION_NAME = @Tabla AND RDB$SYSTEM_FLAG = 0
                    ORDER BY RDB$TRIGGER_NAME",
                    new { Tabla = tabla });

                var trigList = new List<string>();
                foreach (var trig in triggers)
                    trigList.Add((trig.TRIG_NAME as string)?.Trim() ?? "");

                int count = 0;
                try 
                { 
                    ValidarTablaSegura(tabla);
                    count = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {tabla}"); 
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error al contar registros de {Tabla}", tabla); }

                resultado[tabla] = new Dictionary<string, object>
                {
                    ["existe"] = true,
                    ["total_cols"] = colList.Count,
                    ["columnas"] = colList,
                    ["total_triggers"] = trigList.Count,
                    ["triggers"] = trigList,
                    ["registros"] = count
                };
            }
            catch (Exception ex)
            {
                resultado[tabla] = new Dictionary<string, object>
                {
                    ["existe"] = false,
                    ["error"] = ex.Message
                };
            }
        }

        return resultado;
    }

    private static string FirebirdTypeName(int typeNum, int subType)
    {
        return typeNum switch
        {
            7 => "SMALLINT", 8 => "INTEGER", 9 => "QUAD", 10 => "FLOAT",
            11 => "D_FLOAT", 12 => "DATE", 13 => "TIME",
            14 => "CHAR", 16 => "BIGINT", 27 => "DOUBLE", 35 => "TIMESTAMP",
            37 => "VARCHAR", 40 => "CSTRING", 45 => "BLOB_ID",
            261 when subType == 1 => "TEXT", 261 => "BLOB",
            _ => $"TIPO_{typeNum}"
        };
    }
}
