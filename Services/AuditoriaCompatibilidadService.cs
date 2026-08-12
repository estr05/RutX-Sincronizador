// ============================================================================
// ARCHIVO: AuditoriaCompatibilidadService.cs
// PROPOSITO: Porta a C# el auditor auditar_compatibilidad.py (100% SOLO LECTURA).
//            Verifica que una BD Firebird de Microsip tenga todo lo que el
//            sincronizador Rutx necesita: tablas/columnas, triggers, FK,
//            IDs criticos (MicrosipSettings), datos minimos y coherencia
//            vendedor -> cajero -> caja.
//
// USO: recibe la cadena de conexion como parametro (NO la lee de config)
//      para poder auditar la BD que el usuario eligio en el wizard de
//      instalacion o la configurada en el panel web.
// ============================================================================

using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Services;

// ============================================================================
// DTOs DE RESULTADO
// ============================================================================
public class ItemAuditoriaDto
{
    public string Seccion { get; set; } = "";
    public string Item { get; set; } = "";
    public string Estado { get; set; } = "fallo"; // ok | aviso | fallo
    public string Mensaje { get; set; } = "";
}

public class AuditoriaResultadoDto
{
    public List<ItemAuditoriaDto> Items { get; set; } = new();
    public int Ok { get; set; }
    public int Avisos { get; set; }
    public int Fallos { get; set; }
    public DateTime FechaEjecucion { get; set; } = DateTime.UtcNow;
}

public interface IAuditoriaCompatibilidadService
{
    Task<AuditoriaResultadoDto> EjecutarAsync(string connectionString);
}

// ============================================================================
// IMPLEMENTACION
// ============================================================================
public class AuditoriaCompatibilidadService : IAuditoriaCompatibilidadService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuditoriaCompatibilidadService> _logger;

    public AuditoriaCompatibilidadService(IConfiguration configuration,
        ILogger<AuditoriaCompatibilidadService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // TABLAS REQUERIDAS con las columnas que el sincronizador usa en sus SQL
    // (extraidas de RouteService, VentaServicePv, FirebirdAuthService,
    //  CobranzaService, CreditoService, FolioService).
    // Lista vacia = solo se verifica la existencia de la tabla.
    // ------------------------------------------------------------------
    private static readonly Dictionary<string, string[]> TablasRequeridas = new()
    {
        // Catalogos base (objetivo de las FKs de los inserts)
        ["MONEDAS"] = new[] { "MONEDA_ID" },
        ["CONDICIONES_PAGO"] = new[] { "COND_PAGO_ID" },
        ["ALMACENES"] = new[] { "ALMACEN_ID" },
        ["CONCEPTOS_CC"] = new[] { "CONCEPTO_CC_ID" },

        // Cadena de identidad (login nativo Firebird, sin AGENTES)
        ["VENDEDORES"] = new[] { "VENDEDOR_ID", "NOMBRE" },
        ["CAJEROS"] = new[] { "CAJERO_ID", "NOMBRE", "USUARIO", "OPERAR_CAJAS", "ABRIR_CAJAS", "OCULTO" },
        ["CAJAS"] = new[] { "CAJA_ID", "NOMBRE", "ALMACEN_ID", "OCULTO", "FORMA_COBRO_PREDET_ID" },
        ["CAJAS_CAJEROS"] = new[] { "CAJA_ID", "CAJERO_ID", "TIPO_ACCESO" },
        ["MOVTOS_CAJAS"] = new[] { "MOVTO_CAJA_ID", "FECHA", "HORA", "TIPO_MOVTO", "CAJA_ID", "USUARIO_CREADOR" },

        // Catalogos de venta
        ["CLIENTES"] = new[] { "CLIENTE_ID", "NOMBRE", "LIMITE_CREDITO", "COND_PAGO_ID", "VENDEDOR_ID", "ESTATUS" },
        ["DIRS_CLIENTES"] = new[] { "DIR_CLI_ID", "CLIENTE_ID", "CALLE", "COLONIA", "POBLACION", "CODIGO_POSTAL" },
        ["ARTICULOS"] = new[] { "ARTICULO_ID", "NOMBRE", "ESTATUS" },
        ["CLAVES_ARTICULOS"] = new[] { "ARTICULO_ID", "CLAVE_ARTICULO" },
        ["PRECIOS_ARTICULOS"] = new[] { "ARTICULO_ID", "PRECIO", "PRECIO_EMPRESA_ID", "PRECIO_ARTICULO_ID" },
        ["IMPUESTOS"] = new[] { "IMPUESTO_ID", "PCTJE_IMPUESTO" },
        ["IMPUESTOS_ARTICULOS"] = new[] { "ARTICULO_ID", "IMPUESTO_ID" },
        ["SALDOS_IN"] = new[] { "ARTICULO_ID", "ALMACEN_ID", "ENTRADAS_UNIDADES", "SALIDAS_UNIDADES", "ANO", "MES" },
        ["FORMAS_COBRO"] = new[] { "FORMA_COBRO_ID", "NOMBRE", "TIPO" },
        ["SUCURSALES"] = new[] { "SUCURSAL_ID", "NOMBRE", "CALLE", "NOMBRE_CALLE", "POBLACION", "CODIGO_POSTAL" },
        ["RFCS_LCO"] = new[] { "RFC_LCO_ID", "RFC", "NOMBRE_FISCAL", "DOMICILIO_FISCAL", "ESTATUS_VERIFICACION" },
        ["FOLIOS_CAJAS"] = new[] { "CAJA_ID", "TIPO_DOCTO", "SERIE", "CONSECUTIVO" },

        // Documentos de venta PV
        ["DOCTOS_PV"] = new[] { "DOCTO_PV_ID", "CAJA_ID", "TIPO_DOCTO", "SUCURSAL_ID", "FOLIO", "FECHA", "HORA",
                                "CAJERO_ID", "CLIENTE_ID", "ALMACEN_ID", "MONEDA_ID", "ESTATUS", "APLICADO",
                                "VENDEDOR_ID", "IMPORTE_NETO", "TOTAL_IMPUESTOS", "DESCRIPCION", "USUARIO_CREADOR" },
        ["DOCTOS_PV_DET"] = new[] { "DOCTO_PV_DET_ID", "DOCTO_PV_ID", "ARTICULO_ID", "UNIDADES",
                                    "PRECIO_UNITARIO", "PRECIO_UNITARIO_IMPTO", "IMPUESTO_POR_UNIDAD",
                                    "PRECIO_TOTAL_NETO", "POSICION" },
        ["IMPUESTOS_DOCTOS_PV"] = new[] { "DOCTO_PV_ID", "IMPUESTO_ID", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO" },
        ["IMPUESTOS_DOCTOS_PV_DET"] = new[] { "DOCTO_PV_DET_ID", "IMPUESTO_ID", "DOCTO_PV_ID", "PCTJE_IMPUESTO",
                                              "IMPORTE_IMPUESTO", "TIPO_CALC" },
        ["DOCTOS_PV_COBROS"] = new[] { "DOCTO_PV_COBRO_ID", "DOCTO_PV_ID", "TIPO", "FORMA_COBRO_ID", "IMPORTE", "TIPO_CAMBIO" },
        ["DOCTOS_PV_LIGAS"] = new[] { "DOCTO_PV_LIGA_ID", "DOCTO_PV_FTE_ID", "DOCTO_PV_DEST_ID" },

        // CxC / Cobranza
        ["DOCTOS_CC"] = new[] { "DOCTO_CC_ID", "CONCEPTO_CC_ID", "CLIENTE_ID", "IMPORTE_COBRO", "FOLIO",
                                "FECHA", "CANCELADO", "APLICADO" },
        ["SALDOS_CC"] = new[] { "CLIENTE_ID", "CARGOS_CXC", "CREDITOS_CXC" },
        ["VENCIMIENTOS_CARGOS_CC"] = new[] { "DOCTO_CC_ID", "FECHA_VENCIMIENTO" },
        ["DOCTOS_ENTRE_SIS"] = new[] { "CLAVE_SIS_FTE", "DOCTO_FTE_ID", "CLAVE_SIS_DEST", "DOCTO_DEST_ID", "TIPO_DOCTO" },

        // La escribe el trigger de cobros en efectivo
        ["MOVTOS_EFVO_CAJA"] = Array.Empty<string>(),
    };

    // ------------------------------------------------------------------
    // COLUMNAS QUE LOS INSERTS DEL SINCRONIZADOR ESCRIBEN EXPLICITAMENTE
    // (union de los INSERT INTO de VentaServicePv, CobranzaService,
    //  ClienteService y FolioService). Se usan en la auditoria NOT NULL:
    // una columna NOT NULL sin default efectivo que NO este aqui haria
    // fallar los INSERTs (violation NOT NULL).
    // NOTA: las columnas completadas por triggers BEFINS (los IDs con -1)
    // tambien van aqui porque el sync las escribe como -1; el trigger las
    // reemplaza por el valor generado.
    // ------------------------------------------------------------------
    private static readonly Dictionary<string, string[]> ColumnasEscritasPorSync = new()
    {
        ["CLIENTES"] = new[] { "CLIENTE_ID", "NOMBRE", "SUJETO_IEPS", "DIFERIR_CFDI_COBROS",
                                "LIMITE_CREDITO", "MONEDA_ID", "COND_PAGO_ID" },
        ["DOCTOS_PV"] = new[] { "DOCTO_PV_ID", "CAJA_ID", "TIPO_DOCTO", "SUCURSAL_ID", "FOLIO",
                                 "FECHA", "HORA", "CAJERO_ID", "CLIENTE_ID", "DIR_CLI_ID",
                                 "ALMACEN_ID", "MONEDA_ID", "IMPUESTO_INCLUIDO", "TIPO_CAMBIO",
                                 "ESTATUS", "APLICADO", "PROCESO_ORIGEN", "SISTEMA_ORIGEN", "VENDEDOR_ID",
                                 "IMPORTE_NETO", "TOTAL_IMPUESTOS", "TOTAL_RETENCIONES",
                                 "PESO_EMBARQUE", "DESCRIPCION", "ES_CFD", "ENVIADO",
                                 "CFDI_CERTIFICADO", "CARGAR_SUN", "USUARIO_CREADOR",
                                 "FECHA_HORA_CREACION", "PARTIDA_AJUSTE_ID", "PRECIO_ORIG_PARTIDA_AJUSTE" },
        ["DOCTOS_PV_DET"] = new[] { "DOCTO_PV_DET_ID", "DOCTO_PV_ID", "ARTICULO_ID", "UNIDADES",
                                     "PRECIO_UNITARIO", "PRECIO_UNITARIO_IMPTO", "IMPUESTO_POR_UNIDAD",
                                     "PRECIO_TOTAL_NETO", "PRECIO_MODIFICADO", "ROL", "POSICION" },
        ["IMPUESTOS_DOCTOS_PV"] = new[] { "DOCTO_PV_ID", "IMPUESTO_ID", "VENTA_NETA", "VENTA_BRUTA",
                                           "OTROS_IMPUESTOS", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO",
                                           "UNIDADES_IMPUESTO", "IMPORTE_UNITARIO_IMPUESTO" },
        ["IMPUESTOS_DOCTOS_PV_DET"] = new[] { "DOCTO_PV_DET_ID", "IMPUESTO_ID", "DOCTO_PV_ID",
                                               "ID_INTERNO_TIPO_IMPTO", "TIPO_CALC",
                                               "IMPORTE_IMPUESTO_BRUTO", "VENTA_NETA", "VENTA_BRUTA",
                                               "OTROS_IMPUESTOS", "PCTJE_IMPUESTO", "IMPORTE_IMPUESTO",
                                               "UNIDADES_IMPUESTO", "IMPORTE_UNITARIO_IMPUESTO" },
        ["DOCTOS_PV_COBROS"] = new[] { "DOCTO_PV_COBRO_ID", "DOCTO_PV_ID", "TIPO", "FORMA_COBRO_ID",
                                        "IMPORTE", "TIPO_CAMBIO", "IMPORTE_MON_DOC" },
        ["DOCTOS_PV_LIGAS"] = new[] { "DOCTO_PV_LIGA_ID", "DOCTO_PV_FTE_ID", "DOCTO_PV_DEST_ID" },
        ["DOCTOS_CC"] = new[] { "DOCTO_CC_ID", "CONCEPTO_CC_ID", "NATURALEZA_CONCEPTO",
                                 "FOLIO", "SUCURSAL_ID", "FECHA", "HORA",
                                 "CLIENTE_ID", "IMPORTE_COBRO", "TIPO_CAMBIO",
                                 "CANCELADO", "APLICADO", "SISTEMA_ORIGEN",
                                 "ESTATUS", "ESTATUS_ANT", "ES_CFD", "TIENE_ANTICIPO",
                                 "ENVIADO", "CFDI_CERTIFICADO", "MODALIDAD_FACTURACION",
                                 "CONTABILIZADO_GYP", "INTEG_BA", "CONTABILIZADO_BA" },
        ["DOCTOS_ENTRE_SIS"] = new[] { "CLAVE_SIS_FTE", "DOCTO_FTE_ID", "CLAVE_SIS_DEST", "DOCTO_DEST_ID", "TIPO_DOCTO" },
        ["FOLIOS_CAJAS"] = new[] { "CAJA_ID", "TIPO_DOCTO", "SERIE", "CONSECUTIVO" },
    };

    // Tablas que el esquema de Microsip puede tener pero el sincronizador YA
    // NO usa (login nativo Firebird, sin AGENTES ni RUTAS). Solo informativas.
    private static readonly string[] TablasRetiradas = { "AGENTES", "RUTAS", "RUTAS_DET" };

    // ------------------------------------------------------------------
    // TRIGGERS CRITICOS (sin ellos el sincronizador NO puede operar)
    // ------------------------------------------------------------------
    private static readonly string[] TriggersCriticos =
    {
        "DOCTOS_PV_BEFINS",          // Genera DOCTO_PV_ID cuando llega -1
        "DOCTOS_PV_DET_BEFINS",      // Genera DOCTO_PV_DET_ID y POSICION
        "DOCTOS_PV_COBROS_BEFINS",   // Genera DOCTO_PV_COBRO_ID
        "DOCTOS_PV_COBROS_AFTINS_0", // Registra MOVTOS_EFVO_CAJA
        "DOCTOS_PV_AFTUPD_0",        // Dispara APLICA_DOCTO_PV (inventario + CxC)
        "DOCTOS_PV_BEFUPD_0",        // Dispara DESAPLICA_DOCTO_PV (cancelaciones)
        "DOCTOS_PV_LIGAS_BEFINS",    // Genera DOCTO_PV_LIGA_ID (cobranza)
        "DOCTOS_CC_BEFINS",          // Genera DOCTO_CC_ID (cobranza/CxC)
    };

    private const string FkRequerida = "CAJEROS_A_DOCTOS_PV";

    // ==================================================================
    // EJECUCION
    // ==================================================================
    public async Task<AuditoriaResultadoDto> EjecutarAsync(string connectionString)
    {
        var resultado = new AuditoriaResultadoDto();

        try
        {
            await using var connection = new FbConnection(connectionString);
            await connection.OpenAsync();

            SeccionConexion(connection, resultado);
            SeccionTablas(connection, resultado);
            SeccionNotNull(connection, resultado);
            SeccionTriggers(connection, resultado);
            SeccionFk(connection, resultado);
            SeccionIdsCriticos(connection, resultado);
            SeccionDatosMinimos(connection, resultado);
            SeccionCoherencia(connection, resultado);

            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo ejecutar la auditoria de compatibilidad");
            Report(resultado, "Conexion", "Acceso a la BD", "fallo",
                $"No se pudo conectar: {ex.Message}");
        }

        resultado.Ok = resultado.Items.Count(i => i.Estado == "ok");
        resultado.Avisos = resultado.Items.Count(i => i.Estado == "aviso");
        resultado.Fallos = resultado.Items.Count(i => i.Estado == "fallo");
        return resultado;
    }

    private static void SeccionConexion(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        try
        {
            // Detectar version via MON$ o RDB$GET_CONTEXT
            string version = "desconocida";
            try
            {
                using var cmd = connection.CreateCommand();
                // Intentar RDB$GET_CONTEXT (Firebird 3.0+, mas preciso)
                try
                {
                    cmd.CommandText = "SELECT RDB$GET_CONTEXT('SYSTEM', 'ENGINE_VERSION') FROM RDB$DATABASE";
                    var v = cmd.ExecuteScalar()?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(v)) version = v;
                }
                catch
                {
                    // Fallback: MON$DATABASE_VERSION (Firebird 2.5+)
                    try
                    {
                        cmd.CommandText = "SELECT MON$DATABASE_VERSION FROM MON$DATABASE";
                        var v2 = cmd.ExecuteScalar()?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(v2)) version = v2;
                    }
                    catch
                    {
                        // Firebird 2.1 o anterior: solo nombre
                        cmd.CommandText = "SELECT MON$DATABASE_NAME FROM MON$DATABASE";
                        var name = cmd.ExecuteScalar()?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(name)) version = $"({name})";
                    }
                }
            }
            catch { /* sin deteccion de version */ }

            Report(resultado, "Conexion", "Conexion establecida", "ok",
                string.IsNullOrEmpty(version) || version == "desconocida"
                    ? "Conexion establecida"
                    : $"Firebird {version}");
        }
        catch
        {
            Report(resultado, "Conexion", "Conexion establecida", "ok", "Conexion establecida");
        }
    }

    // ------------------------------------------------------------------
    // 1. TABLAS Y COLUMNAS
    // ------------------------------------------------------------------
    private static void SeccionTablas(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        HashSet<string> tablas;
        try
        {
            tablas = connection.Query<string>(
                "SELECT TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS " +
                "WHERE RDB$SYSTEM_FLAG = 0 AND RDB$VIEW_BLR IS NULL")
                .Select(t => t.Trim().ToUpperInvariant()).ToHashSet();
        }
        catch (Exception ex)
        {
            Report(resultado, "Tablas", "Metadatos", "fallo",
                $"No se pudieron leer las tablas: {ex.Message}");
            return;
        }

        foreach (var (tabla, columnas) in TablasRequeridas)
        {
            var t = tabla.ToUpperInvariant();
            if (!tablas.Contains(t))
            {
                Report(resultado, "Tablas", $"Tabla {tabla}", "fallo", "NO EXISTE en la BD");
                continue;
            }

            if (columnas.Length == 0)
            {
                Report(resultado, "Tablas", $"Tabla {tabla}", "ok", "existe");
                continue;
            }

            var faltantes = ObtenerColumnasFaltantes(connection, t, columnas);
            if (faltantes.Count == 0)
                Report(resultado, "Tablas", $"Tabla {tabla}", "ok",
                    $"existe con sus {columnas.Length} columnas clave");
            else
                Report(resultado, "Tablas", $"Tabla {tabla}", "fallo",
                    $"existe pero FALTAN columnas: {string.Join(", ", faltantes)}");
        }

        // Tablas retiradas: solo informativas
        foreach (var tabla in TablasRetiradas)
        {
            if (tablas.Contains(tabla))
                Report(resultado, "Tablas", $"Tabla {tabla}", "aviso",
                    "existe pero el sincronizador YA NO la usa (login nativo Firebird)");
            else
                Report(resultado, "Tablas", $"Tabla {tabla}", "aviso",
                    "no existe (no se necesita: tabla retirada del flujo)");
        }
    }

    private static List<string> ObtenerColumnasFaltantes(FbConnection connection, string tabla, string[] columnas)
    {
        try
        {
            var existentes = connection.Query<string>(
                "SELECT TRIM(RDB$FIELD_NAME) FROM RDB$RELATION_FIELDS WHERE RDB$RELATION_NAME = @t",
                new { t = tabla })
                .Select(c => c.Trim().ToUpperInvariant()).ToHashSet();
            return columnas.Where(c => !existentes.Contains(c.ToUpperInvariant())).ToList();
        }
        catch
        {
            return columnas.ToList();
        }
    }

    // ------------------------------------------------------------------
    // 1b. NOT NULL EFECTIVO (resolviendo el default del DOMINIO)
    // ------------------------------------------------------------------
    // Busca en las tablas de escritura las columnas NOT NULL SIN default
    // efectivo (ni de columna ni del dominio RDB$FIELDS). Si el sincronizador
    // no las escribe explicitamente en sus INSERTs, las ventas/cobranzas
    // fallarian con violacion NOT NULL. Validado contra CHOCOLATES (FB3) y
    // CRUZROJAS (FB5): en ambos el resultado es identico (universal).
    private static void SeccionNotNull(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        try
        {
            var tablas = ColumnasEscritasPorSync.Keys.ToList();
            var filas = connection.Query<ColumnaNotNullFila>(
                "SELECT TRIM(rf.RDB$RELATION_NAME) AS Tabla, TRIM(rf.RDB$FIELD_NAME) AS Columna " +
                "FROM RDB$RELATION_FIELDS rf " +
                "JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE " +
                "WHERE rf.RDB$NULL_FLAG = 1 " +
                "  AND rf.RDB$DEFAULT_SOURCE IS NULL " +
                "  AND f.RDB$DEFAULT_SOURCE IS NULL " +
                "  AND TRIM(rf.RDB$RELATION_NAME) IN @Tablas",
                new { Tablas = tablas }).ToList();

            var porTabla = ClasificarColumnasSinDefault(filas.Select(f => (f.Tabla, f.Columna)));

            if (porTabla.Count == 0)
            {
                Report(resultado, "NOT NULL", "Columnas obligatorias sin default", "ok",
                    "todas las columnas NOT NULL de las tablas de escritura tienen un default efectivo " +
                    "(de columna o del dominio) o las escribe el sincronizador");
                return;
            }

            foreach (var riesgo in porTabla)
            {
                if (riesgo.NoEscritas.Count > 0)
                {
                    Report(resultado, "NOT NULL", $"Tabla {riesgo.Tabla}", "fallo",
                        $"columnas NOT NULL sin default que el sincronizador NO escribe al insertar: " +
                        $"{string.Join(", ", riesgo.NoEscritas)}. Sus INSERTs fallarian con violacion NOT NULL " +
                        $"a menos que un trigger de la BD la complete. Revisa si la version de Microsip " +
                        $"agrego columnas nuevas.");
                }
                else
                {
                    Report(resultado, "NOT NULL", $"Tabla {riesgo.Tabla}", "ok",
                        $"las {riesgo.SinDefault.Count} columna(s) NOT NULL sin default " +
                        $"({string.Join(", ", riesgo.SinDefault)}) el sincronizador las escribe explicitamente");
                }
            }
        }
        catch (Exception ex)
        {
            Report(resultado, "NOT NULL", "Metadatos", "aviso", $"no verificable: {ex.Message}");
        }
    }

    /// <summary>Resultado por tabla de la clasificacion de columnas sin default.</summary>
    public sealed record RiesgoNotNullTabla(string Tabla, List<string> SinDefault, List<string> NoEscritas);

    /// <summary>
    /// Clasifica las columnas NOT NULL sin default efectivo por tabla, marcando
    /// las que el sincronizador NO escribe en sus INSERTs (riesgo de fallo).
    /// Separado del SQL para poder probarlo de forma unitaria.
    /// </summary>
    public static List<RiesgoNotNullTabla> ClasificarColumnasSinDefault(
        IEnumerable<(string Tabla, string Columna)> sinDefault)
    {
        var porTabla = sinDefault
            .Select(x => (Tabla: x.Tabla.Trim().ToUpperInvariant(), Columna: x.Columna.Trim().ToUpperInvariant()))
            .GroupBy(x => x.Tabla)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        var resultado = new List<RiesgoNotNullTabla>();
        foreach (var grupo in porTabla)
        {
            var escritas = (ColumnasEscritasPorSync.TryGetValue(grupo.Key, out var set) ? set : Array.Empty<string>())
                .Select(c => c.ToUpperInvariant()).ToHashSet();
            var todas = grupo.Select(g => g.Columna).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            var noEscritas = todas.Where(c => !escritas.Contains(c)).ToList();
            resultado.Add(new RiesgoNotNullTabla(grupo.Key, todas, noEscritas));
        }
        return resultado;
    }

    private sealed class ColumnaNotNullFila
    {
        public string Tabla { get; set; } = "";
        public string Columna { get; set; } = "";
    }

    // ------------------------------------------------------------------
    // 2. TRIGGERS CRITICOS
    // ------------------------------------------------------------------
    private static void SeccionTriggers(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        try
        {
            var triggers = connection.Query<string>("SELECT TRIM(RDB$TRIGGER_NAME) FROM RDB$TRIGGERS")
                .Select(t => t.Trim().ToUpperInvariant()).ToHashSet();

            foreach (var trig in TriggersCriticos)
            {
                if (triggers.Contains(trig))
                    Report(resultado, "Triggers", $"Trigger {trig}", "ok", "existe");
                else
                    Report(resultado, "Triggers", $"Trigger {trig}", "fallo",
                        "NO EXISTE. Revisar si la BD tiene el esquema completo de Microsip PV");
            }
        }
        catch (Exception ex)
        {
            Report(resultado, "Triggers", "Metadatos", "fallo",
                $"No se pudieron leer los triggers: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 3. FK USADA POR FkResolverService
    // ------------------------------------------------------------------
    private static void SeccionFk(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        try
        {
            var fks = connection.Query<string>(
                "SELECT TRIM(RDB$CONSTRAINT_NAME) FROM RDB$RELATION_CONSTRAINTS " +
                "WHERE RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'")
                .Select(f => f.Trim().ToUpperInvariant()).ToHashSet();

            if (fks.Contains(FkRequerida))
                Report(resultado, "FK", $"Constraint {FkRequerida}", "ok", "existe en metadatos");
            else
                Report(resultado, "FK", $"Constraint {FkRequerida}", "aviso",
                    "NO EXISTE -> FkResolver usara fallback (DefaultCajeroId). " +
                    "No rompe el flujo si cada usuario tiene cajero por USUARIO.");
        }
        catch (Exception ex)
        {
            Report(resultado, "FK", FkRequerida, "aviso", $"no verificable: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 4. IDS CRITICOS (MicrosipSettings) + generador ID_DOCTOS
    // ------------------------------------------------------------------
    private void SeccionIdsCriticos(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        var config = _configuration.GetSection("MicrosipSettings");
        int monedaId = config.GetValue("DefaultMonedaId", 1);
        int condPagoId = config.GetValue("DefaultCondPagoId", 1);
        int impuestoId = config.GetValue("DefaultImpuestoId", 622);
        int formaCobroId = config.GetValue("DefaultFormaCobroId", 67);
        int sucursalId = config.GetValue("DefaultSucursalId", 4274);
        int almacenId = config.GetValue("DefaultAlmacenId", 19);
        int cajeroId = config.GetValue("DefaultCajeroId", 2419);
        int conceptoCobroId = config.GetValue("DefaultConceptoCobroId", 11);
        var creditFormas = config.GetSection("CreditFormaCobroIds").Get<int[]>() ?? Array.Empty<int>();

        var ids = new (string Tabla, string Columna, int Id, string Desc, bool EsAviso)[]
        {
            ("MONEDAS", "MONEDA_ID", monedaId, "Moneda base", false),
            ("CONDICIONES_PAGO", "COND_PAGO_ID", condPagoId, "Condicion de pago por defecto", false),
            ("IMPUESTOS", "IMPUESTO_ID", impuestoId, "Impuesto por defecto", false),
            ("FORMAS_COBRO", "FORMA_COBRO_ID", formaCobroId, "Forma de cobro contado", false),
            ("SUCURSALES", "SUCURSAL_ID", sucursalId, "Sucursal por defecto", false),
            ("ALMACENES", "ALMACEN_ID", almacenId, "Almacen por defecto", false),
            ("CAJEROS", "CAJERO_ID", cajeroId, "Cajero fallback (solo si no hay cajero por USUARIO)", true),
            ("CONCEPTOS_CC", "CONCEPTO_CC_ID", conceptoCobroId, "Concepto de cobro (abono CxC)", false),
        };

        foreach (var (tabla, columna, id, desc, esAviso) in ids)
        {
            try
            {
                var existe = connection.ExecuteScalar<int>(
                    $"SELECT COUNT(*) FROM {tabla} WHERE {columna} = @Id", new { Id = id }) > 0;
                if (existe)
                    Report(resultado, "IDs", $"{tabla}.{columna}={id}", "ok", desc);
                else
                    Report(resultado, "IDs", $"{tabla}.{columna}={id}", esAviso ? "aviso" : "fallo",
                        $"{desc} -> NO ENCONTRADO en la BD");
            }
            catch (Exception ex)
            {
                Report(resultado, "IDs", $"{tabla}.{columna}={id}", "aviso", $"no verificable: {ex.Message}");
            }
        }

        foreach (var id in creditFormas)
        {
            try
            {
                var existe = connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM FORMAS_COBRO WHERE FORMA_COBRO_ID = @Id", new { Id = id }) > 0;
                if (existe)
                    Report(resultado, "IDs", $"FORMAS_COBRO.FORMA_COBRO_ID={id}", "ok",
                        "Forma de cobro a credito");
                else
                    Report(resultado, "IDs", $"FORMAS_COBRO.FORMA_COBRO_ID={id}", "aviso",
                        "Forma de cobro a credito -> no existe (solo afecta ventas a credito)");
            }
            catch (Exception ex)
            {
                Report(resultado, "IDs", $"FORMAS_COBRO.FORMA_COBRO_ID={id}", "aviso",
                    $"no verificable: {ex.Message}");
            }
        }

        // Generador usado por los triggers BEFINS
        try
        {
            var generador = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM RDB$GENERATORS WHERE RDB$SYSTEM_FLAG = 0 " +
                "AND TRIM(RDB$GENERATOR_NAME) = 'ID_DOCTOS'") > 0;
            if (generador)
                Report(resultado, "IDs", "Generador ID_DOCTOS", "ok",
                    "existe (los triggers BEFINS podran generar IDs)");
            else
                Report(resultado, "IDs", "Generador ID_DOCTOS", "fallo",
                    "NO EXISTE -> los triggers de generacion de IDs fallaran");
        }
        catch (Exception ex)
        {
            Report(resultado, "IDs", "Generador ID_DOCTOS", "aviso", $"no verificable: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // 5. DATOS MINIMOS PARA OPERAR
    // ------------------------------------------------------------------
    private void SeccionDatosMinimos(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        bool autoCrearFolios = _configuration.GetValue("MicrosipSettings:AutoCrearFolios", true);

        var checks = new (string Item, string Sql, int Minimo, bool EsCritico)[]
        {
            ("Al menos 1 caja activa (OCULTO='N')",
                "SELECT COUNT(*) FROM CAJAS WHERE OCULTO = 'N'", 1, true),
            ("Al menos 1 vendedor",
                "SELECT COUNT(*) FROM VENDEDORES", 1, true),
            ("Al menos 1 cajero con USUARIO",
                "SELECT COUNT(*) FROM CAJEROS WHERE USUARIO IS NOT NULL AND TRIM(USUARIO) <> ''", 1, true),
            ("Al menos 1 acceso de cajero a caja (CAJAS_CAJEROS)",
                "SELECT COUNT(*) FROM CAJAS_CAJEROS WHERE TIPO_ACCESO IN ('A','O')", 1, true),
            ("Al menos 1 cliente activo (ESTATUS='A')",
                "SELECT COUNT(*) FROM CLIENTES WHERE ESTATUS = 'A'", 1, true),
            ("Al menos 1 cliente con direccion en DIRS_CLIENTES",
                "SELECT COUNT(DISTINCT CLIENTE_ID) FROM DIRS_CLIENTES", 1, true),
            ("Al menos 1 articulo activo (ESTATUS='A')",
                "SELECT COUNT(*) FROM ARTICULOS WHERE ESTATUS = 'A'", 1, true),
            ("Al menos 1 articulo con precio (PRECIOS_ARTICULOS)",
                "SELECT COUNT(DISTINCT ARTICULO_ID) FROM PRECIOS_ARTICULOS", 1, true),
            ("Al menos 1 forma de cobro en catalogo",
                "SELECT COUNT(*) FROM FORMAS_COBRO", 1, true),
            ("Folio de ventas 'V' en FOLIOS_CAJAS",
                "SELECT COUNT(*) FROM FOLIOS_CAJAS WHERE TIPO_DOCTO = 'V'", 1, !autoCrearFolios),
            ("Al menos 1 emisor fiscal activo (RFCS_LCO)",
                "SELECT COUNT(*) FROM RFCS_LCO WHERE ESTATUS_VERIFICACION = 'A'", 1, false),
            ("Al menos 1 impuesto en catalogo",
                "SELECT COUNT(*) FROM IMPUESTOS", 1, false),
            ("Al menos 1 articulo con existencias > 0 (SALDOS_IN)",
                "SELECT COUNT(DISTINCT ARTICULO_ID) FROM SALDOS_IN WHERE (ENTRADAS_UNIDADES - SALIDAS_UNIDADES) > 0", 1, false),
        };

        foreach (var (item, sql, minimo, esCritico) in checks)
        {
            try
            {
                var count = connection.ExecuteScalar<int>(sql);
                if (count >= minimo)
                    Report(resultado, "Datos", item, "ok", $"{count} registro(s)");
                else
                    Report(resultado, "Datos", item, esCritico ? "fallo" : "aviso",
                        $"solo {count} (minimo {minimo})");
            }
            catch (Exception ex)
            {
                Report(resultado, "Datos", item, esCritico ? "fallo" : "aviso",
                    $"error de consulta: {ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------
    // 6. COHERENCIA VENDEDOR -> CAJERO -> CAJA
    // ------------------------------------------------------------------
    private static void SeccionCoherencia(FbConnection connection, AuditoriaResultadoDto resultado)
    {
        try
        {
            var cajerosConUsuario = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM CAJEROS WHERE USUARIO IS NOT NULL AND TRIM(USUARIO) <> ''");
            var cajerosSinAccesoOperar = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM CAJEROS c WHERE c.USUARIO IS NOT NULL AND TRIM(c.USUARIO) <> '' " +
                "AND NOT EXISTS (SELECT 1 FROM CAJAS_CAJEROS cc " +
                "WHERE cc.CAJERO_ID = c.CAJERO_ID AND cc.TIPO_ACCESO = 'O')");

            if (cajerosConUsuario > 0)
                Report(resultado, "Coherencia", "Cajeros con USUARIO", "ok", $"{cajerosConUsuario} cajero(s)");
            else
                Report(resultado, "Coherencia", "Cajeros con USUARIO", "fallo",
                    "no hay cajeros con USUARIO -> la app no podra resolver cajero en el login");

            if (cajerosSinAccesoOperar == 0)
                Report(resultado, "Coherencia", "Acceso OPERAR a cajas", "ok",
                    "todos los cajeros con USUARIO tienen acceso 'O' a alguna caja");
            else
                Report(resultado, "Coherencia", "Acceso OPERAR a cajas", "aviso",
                    $"{cajerosSinAccesoOperar} cajero(s) con USUARIO sin acceso 'O' a ninguna caja");
        }
        catch (Exception ex)
        {
            Report(resultado, "Coherencia", "Cadena vendedor-cajero-caja", "aviso",
                $"no verificable: {ex.Message}");
        }
    }

    // ==================================================================
    // HELPERS
    // ==================================================================
    private static void Report(AuditoriaResultadoDto resultado, string seccion, string item,
        string estado, string mensaje)
    {
        resultado.Items.Add(new ItemAuditoriaDto
        {
            Seccion = seccion,
            Item = item,
            Estado = estado,
            Mensaje = mensaje
        });
    }
}
