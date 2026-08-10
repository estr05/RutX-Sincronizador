// ============================================================================
// ARCHIVO: RouteService.cs
// PROPOSITO: Servicio para la gestion de Ventas en Ruta
//            Microsip ERP - CHOCOLATES.FDB (Firebird 3.0)
//
// ETAPAS DEL FLUJO DE RUTA:
//   ETAPA 1 - SINCRONIZACION MATUTINA (ObtenerSyncMatutino)
//   ETAPA 2 - VENTAS DEL DIA         (VentaServicePv)
//   ETAPA 3 - CIERRE DE RUTA         (ObtenerResumenDiario + CerrarRuta)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Servicio para gestionar el flujo completo de Ventas en Ruta.
/// </summary>
public class RouteService : IRouteService, IDebugRouteService
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly ILogger<RouteService> _logger;

    public RouteService(
        IConfiguration configuration,
        ILogger<RouteService> logger)
    {
        _configuration = configuration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
    }

    // =========================================================================
    // ETAPA 1: SINCRONIZACION MATUTINA
    // =========================================================================
    public async Task<SyncMorningRutaResponseDto> ObtenerSyncMatutinoAsync(UsuarioSesion sesion)
    {
        // Ejecutar las consultas en paralelo para minimizar latencia
        var clientesTask = ObtenerClientesPorVendedorAsync(sesion.VendedorId);
        var productosTask = ObtenerProductosActivosAsync(sesion.AlmacenId);
        var formasCobroTask = ObtenerFormasCobroAsync();
        var emisorTask = ObtenerEmisorAsync();

        await Task.WhenAll(clientesTask, productosTask, formasCobroTask, emisorTask);

        var clientes = (await clientesTask).ToList();
        var productos = (await productosTask).ToList();
        var formasCobro = (await formasCobroTask).ToList();
        var emisor = await emisorTask;

        // Caja resuelta en el login (claims del token)
        CajaInfo caja = sesion.CajaId > 0
            ? await ObtenerCajaInfoAsync(sesion.CajaId)
            : new CajaInfo
            {
                CajaId = 0,
                Nombre = "SIN CAJA",
                AlmacenId = sesion.AlmacenId
            };

        // Datos de la sucursal del usuario
        SucursalDto? sucursal = null;
        if (sesion.SucursalId > 0)
        {
            sucursal = await ObtenerSucursalAsync(sesion.SucursalId);
        }

        return new SyncMorningRutaResponseDto
        {
            Mensaje = "Sincronizacion matutina exitosa",
            Vendedor = new VendedorRutaInfo
            {
                VendedorId = sesion.VendedorId,
                Nombre = sesion.VendedorNombre,
                Usuario = sesion.Usuario,
                SucursalId = sesion.SucursalId,
                AlmacenId = sesion.AlmacenId
            },
            Ruta = null,
            Caja = caja,
            Clientes = clientes,
            Productos = productos,
            FormasCobro = formasCobro,
            Emisor = emisor,
            Sucursal = sucursal
        };
    }

    /// <summary>
    /// Obtiene la informacion de la caja resuelta en el login (desde CAJAS).
    /// </summary>
    private async Task<CajaInfo> ObtenerCajaInfoAsync(int cajaId)
    {
        using var connection = new FbConnection(_connectionString);
        const string sql = @"
            SELECT CAJA_ID, NOMBRE, ALMACEN_ID, FORMA_COBRO_PREDET_ID,
                   MANEJAR_VENDEDORES
            FROM CAJAS WHERE CAJA_ID = @CajaId";

        var result = await connection.QueryFirstOrDefaultAsync(sql, new { CajaId = cajaId });
        if (result == null)
        {
            return new CajaInfo { CajaId = 0, Nombre = "SIN CAJA", AlmacenId = 0 };
        }

        return new CajaInfo
        {
            CajaId = Convert.ToInt32(result.CAJA_ID),
            Nombre = result.NOMBRE?.ToString()?.Trim() ?? "",
            AlmacenId = Convert.ToInt32(result.ALMACEN_ID),
            FormaCobroPredetId = result.FORMA_COBRO_PREDET_ID != null
                ? Convert.ToInt32(result.FORMA_COBRO_PREDET_ID) : null,
            ManejaVendedores = result.MANEJAR_VENDEDORES?.ToString()?.Trim() == "S"
        };
    }

    /// <summary>
    /// Obtiene los clientes activos asignados al vendedor.
    ///
    /// FLUJO DE DATOS:
    ///   CLIENTES.VENDEDOR_ID = VendedorId (asignacion directa del vendedor)
    ///
    /// NOTA: 
    ///   - Ya no se usa AGENTES/RUTAS/RUTAS_DET para el mapeo (se elimino
    ///     la dependencia de AGENTES en el login y en la sincronizacion).
    ///   - Subconsultas escalares (SELECT FIRST 1) para obtener solo UNA
    ///     direccion por cliente, evitando duplicados cuando un cliente
    ///     tiene varias direcciones en DIRS_CLIENTES.
    /// </summary>
    private async Task<IEnumerable<ClienteDto>> ObtenerClientesPorVendedorAsync(int vendedorId)
    {
        using var connection = new FbConnection(_connectionString);
        const string sql = @"
            SELECT c.CLIENTE_ID AS ClienteId,
                   c.NOMBRE AS NombreCliente,
                   c.LIMITE_CREDITO AS LimiteCredito,
                   c.COND_PAGO_ID AS CondPagoId,
                   (SELECT COALESCE(SUM(sc.CARGOS_CXC), 0) - COALESCE(SUM(sc.CREDITOS_CXC), 0)
                    FROM SALDOS_CC sc
                    WHERE sc.CLIENTE_ID = c.CLIENTE_ID) AS Saldo,
                   (SELECT FIRST 1 d.CALLE FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS Calle,
                   (SELECT FIRST 1 d.COLONIA FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS Colonia,
                   (SELECT FIRST 1 d.POBLACION FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS Poblacion,
                   (SELECT FIRST 1 d.CODIGO_POSTAL FROM DIRS_CLIENTES d
                    WHERE d.CLIENTE_ID = c.CLIENTE_ID) AS CodigoPostal
            FROM CLIENTES c
            WHERE c.VENDEDOR_ID = @VendedorId
              AND c.ESTATUS = 'A'
            ";

        // Regla identica a la validacion de la venta (VentaServicePv):
        // limite <= 0 = sin restriccion (puede); si no, puede solo si saldo < limite.
        var clientes = (await connection.QueryAsync<ClienteDto>(sql,
            new { VendedorId = vendedorId })).ToList();

        foreach (var cliente in clientes)
            cliente.PuedeCredito = cliente.LimiteCredito <= 0
                || cliente.Saldo < cliente.LimiteCredito;

        return clientes;
    }

    /// <summary>
    /// Obtiene el catalogo de productos con existencias > 0 en el almacen
    /// de la caja del usuario (SALDOS_IN, periodo vigente) y con sus
    /// impuestos REALES (impuestos compuestos).
    ///
    /// DECISION DEL EQUIPO: el catalogo solo incluye productos con
    /// carga disponible (existencias > 0) en el almacen del vendedor.
    ///
    /// CAMPOS:
    ///   - precio: SIN impuestos (PRECIOS_ARTICULOS, lista 42)
    ///   - precio_con_impuesto: precio * PRODUCTO(1 + pctje/100)
    ///   - impuestos: lista real de IMPUESTOS_ARTICULOS
    ///   - existencias: balance de SALDOS_IN del periodo vigente
    /// </summary>
    private async Task<IEnumerable<ProductoDto>> ObtenerProductosActivosAsync(int almacenId)
    {
        using var connection = new FbConnection(_connectionString);
        int precioEmpresaId = _configuration.GetValue<int?>("MicrosipSettings:DefaultPrecioEmpresaId") ?? 42;

        const string sql = @"
            SELECT
                a.ARTICULO_ID AS ArticuloId,
                (SELECT FIRST 1 ca.CLAVE_ARTICULO 
                 FROM CLAVES_ARTICULOS ca 
                 WHERE ca.ARTICULO_ID = a.ARTICULO_ID 
                 ORDER BY ca.CLAVE_ARTICULO) AS Clave,
                a.NOMBRE AS Nombre,
                a.ESTATUS AS Estatus,
                COALESCE(
                    (SELECT FIRST 1 pa.PRECIO
                     FROM PRECIOS_ARTICULOS pa
                     WHERE pa.ARTICULO_ID = a.ARTICULO_ID
                       AND pa.PRECIO_EMPRESA_ID = @PrecioEmpresaId
                     ORDER BY pa.PRECIO_ARTICULO_ID),
                    (SELECT FIRST 1 pa.PRECIO
                     FROM PRECIOS_ARTICULOS pa
                     WHERE pa.ARTICULO_ID = a.ARTICULO_ID
                     ORDER BY pa.PRECIO_ARTICULO_ID),
                    0
                ) AS Precio,
                COALESCE(
                    (SELECT FIRST 1 (s.ENTRADAS_UNIDADES - s.SALIDAS_UNIDADES)
                     FROM SALDOS_IN s
                     WHERE s.ARTICULO_ID = a.ARTICULO_ID
                       AND s.ALMACEN_ID = @AlmacenId
                     ORDER BY s.ANO DESC, s.MES DESC),
                    0
                ) AS Existencias
            FROM ARTICULOS a
            WHERE a.ESTATUS = 'A'
              AND (
                    SELECT FIRST 1 (s.ENTRADAS_UNIDADES - s.SALIDAS_UNIDADES)
                    FROM SALDOS_IN s
                    WHERE s.ARTICULO_ID = a.ARTICULO_ID
                      AND s.ALMACEN_ID = @AlmacenId
                    ORDER BY s.ANO DESC, s.MES DESC
              ) > 0
            ORDER BY a.NOMBRE";

        var rows = (await connection.QueryAsync<ProductoRawRow>(sql,
            new { AlmacenId = almacenId, PrecioEmpresaId = precioEmpresaId })).ToList();

        var productos = new List<ProductoDto>();
        if (rows.Count == 0)
            return productos;

        var articuloIds = rows.Select(r => r.ArticuloId).ToList();

        // Cargar los impuestos reales de todos los articulos en una sola consulta
        var impuestosPorArticulo = new Dictionary<int, List<(int ImpuestoId, decimal Pctje)>>();
        var filasImpuestos = await connection.QueryAsync<ImpuestoRawRow>(@"
            SELECT ia.ARTICULO_ID AS ArticuloId, ia.IMPUESTO_ID AS ImpuestoId, i.PCTJE_IMPUESTO AS PctjeImpuesto
            FROM IMPUESTOS_ARTICULOS ia
            INNER JOIN IMPUESTOS i ON i.IMPUESTO_ID = ia.IMPUESTO_ID
            WHERE ia.ARTICULO_ID IN @Ids",
            new { Ids = articuloIds });

        foreach (var fi in filasImpuestos)
        {
            if (!impuestosPorArticulo.ContainsKey(fi.ArticuloId))
                impuestosPorArticulo[fi.ArticuloId] = new List<(int, decimal)>();
            impuestosPorArticulo[fi.ArticuloId].Add((fi.ImpuestoId, fi.PctjeImpuesto));
        }

        foreach (var r in rows)
        {
            int articuloId = r.ArticuloId;
            string clave = !string.IsNullOrWhiteSpace(r.Clave) ? r.Clave.Trim() : articuloId.ToString();
            double precio = r.Precio.HasValue ? Convert.ToDouble(r.Precio.Value) : 0.0;

            // Impuestos del articulo ordenados por tasa descendente (IVA primero)
            var impuestos = impuestosPorArticulo.TryGetValue(articuloId, out var lista)
                ? lista.OrderByDescending(t => t.Pctje).ToList()
                : new List<(int ImpuestoId, decimal Pctje)> { (622, 16m) };

            // Precio con impuestos COMPUESTOS: precio * PRODUCTO(1 + pctje/100)
            decimal factor = 1m;
            foreach (var (_, pct) in impuestos)
                factor *= 1m + pct / 100m;

            var impuestoPrincipal = impuestos.First();

            productos.Add(new ProductoDto
            {
                ArticuloId = articuloId,
                Clave = clave,
                Nombre = r.Nombre?.Trim() ?? "",
                Estatus = r.Estatus?.Trim() ?? "A",
                Precio = precio,
                PrecioConImpuesto = Math.Round(precio * (double)factor, 2),
                PorcentajeImpuesto = (int)impuestoPrincipal.Pctje,
                ImpuestoId = impuestoPrincipal.ImpuestoId,
                Existencias = r.Existencias.HasValue ? Convert.ToDouble(r.Existencias.Value) : 0.0,
                Impuestos = impuestos.Select(t => new ImpuestoProductoDto
                {
                    ImpuestoId = t.ImpuestoId,
                    PctjeImpuesto = t.Pctje
                }).ToList()
            });
        }

        return productos;
    }

    private class ProductoRawRow
    {
        public int ArticuloId { get; set; }
        public string? Clave { get; set; }
        public string? Nombre { get; set; }
        public string? Estatus { get; set; }
        public decimal? Precio { get; set; }
        public decimal? Existencias { get; set; }
    }

    private class ImpuestoRawRow
    {
        public int ArticuloId { get; set; }
        public int ImpuestoId { get; set; }
        public decimal PctjeImpuesto { get; set; }
    }

    private async Task<IEnumerable<FormaCobroDto>> ObtenerFormasCobroAsync()
    {
        using var connection = new FbConnection(_connectionString);
        const string sql = @"
            SELECT FORMA_COBRO_ID AS FormaCobroId, NOMBRE AS Nombre, TIPO AS Tipo
            FROM FORMAS_COBRO ORDER BY FORMA_COBRO_ID";

        return await connection.QueryAsync<FormaCobroDto>(sql);
    }

    /// <summary>
    /// Obtiene los datos fiscales del emisor (RFC, razon social, domicilio fiscal).
    /// Se consulta desde RFCS_LCO, que almacena los RFCs registrados en el sistema.
    /// Se toma el primer RFC activo (ESTATUS_VERIFICACION = 'A') como el RFC del emisor.
    /// 
    /// Pendiente: Obtener REGIMEN_FISCAL desde MGFACTURA_CONFIG
    /// </summary>
    private async Task<EmisorFiscalDto?> ObtenerEmisorAsync()
    {
        using var connection = new FbConnection(_connectionString);
        const string sql = @"
            SELECT FIRST 1
                RFC AS Rfc,
                NOMBRE_FISCAL AS NombreFiscal,
                DOMICILIO_FISCAL AS DomicilioFiscal
            FROM RFCS_LCO
            WHERE ESTATUS_VERIFICACION = 'A'
            ORDER BY RFC_LCO_ID";

        var result = await connection.QueryFirstOrDefaultAsync<EmisorFiscalDto>(sql);
        return result;
    }

    /// <summary>
    /// Obtiene los datos del establecimiento (sucursal) donde se realiza la venta.
    /// Consulta SUCURSALES por ID y regresa nombre, direccion completa y telefono.
    /// 
    /// CALLE y NOMBRE_CALLE: en Microsip, CALLE puede ser el tipo de via
    /// (Calle, Avenida, etc.) y NOMBRE_CALLE el nombre real. Se concatenan
    /// para formar la direccion completa.
    /// </summary>
    private async Task<SucursalDto?> ObtenerSucursalAsync(int sucursalId)
    {
        using var connection = new FbConnection(_connectionString);
        const string sql = @"
            SELECT
                SUCURSAL_ID AS SucursalId,
                NOMBRE AS Nombre,
                CALLE AS Calle,
                NOMBRE_CALLE AS NombreCalle,
                NUM_EXTERIOR AS NumExterior,
                NUM_INTERIOR AS NumInterior,
                COLONIA AS Colonia,
                POBLACION AS Poblacion,
                CODIGO_POSTAL AS CodigoPostal,
                TELEFONO1 AS Telefono
            FROM SUCURSALES
            WHERE SUCURSAL_ID = @SucursalId";

        var result = await connection.QueryFirstOrDefaultAsync<dynamic>(sql, new { SucursalId = sucursalId });
        if (result == null) return null;

        return new SucursalDto
        {
            SucursalId = Convert.ToInt32(result.SUCURSAL_ID),
            Nombre = result.NOMBRE?.ToString()?.Trim() ?? "",
            // Concatenar CALLE + NOMBRE_CALLE si ambos existen
            Calle = (result.NOMBRE_CALLE?.ToString()?.Trim() ?? "") != ""
                ? $"{result.CALLE?.ToString()?.Trim() ?? ""} {result.NOMBRE_CALLE?.ToString()?.Trim() ?? ""}".Trim()
                : (result.CALLE?.ToString()?.Trim() ?? ""),
            NumExterior = result.NUM_EXTERIOR?.ToString()?.Trim() ?? "",
            NumInterior = result.NUM_INTERIOR?.ToString()?.Trim() ?? "",
            Colonia = result.COLONIA?.ToString()?.Trim() ?? "",
            Poblacion = result.POBLACION?.ToString()?.Trim() ?? "",
            CodigoPostal = result.CODIGO_POSTAL?.ToString()?.Trim() ?? "",
            Telefono = result.TELEFONO1?.ToString()?.Trim() ?? ""
        };
    }

    // =========================================================================
    // ETAPA 3 - PRE-CIERRE: OBTENER RESUMEN DIARIO
    // =========================================================================
    /// <summary>
    /// Construye la condicion SQL de identidad para filtrar DOCTOS_PV del usuario.
    /// Prioridad: USUARIO_CREADOR (usuario nativo de Microsip, viaja en cada venta PV).
    /// Fallback: CAJA_ID y/o CAJERO_ID resueltos en el login.
    /// </summary>
    private static (string Condicion, object Parametros) CondicionIdentidad(UsuarioSesion sesion, DateTime fecha)
    {
        if (!string.IsNullOrWhiteSpace(sesion.Usuario))
            return (
                "pv.USUARIO_CREADOR = @Usuario AND pv.FECHA = @Fecha",
                new { Usuario = sesion.Usuario, Fecha = fecha.Date });

        if (sesion.CajaId > 0 && sesion.CajeroId > 0)
            return (
                "pv.CAJA_ID = @CajaId AND pv.CAJERO_ID = @CajeroId AND pv.FECHA = @Fecha",
                new { CajaId = sesion.CajaId, CajeroId = sesion.CajeroId, Fecha = fecha.Date });

        if (sesion.CajaId > 0)
            return (
                "pv.CAJA_ID = @CajaId AND pv.FECHA = @Fecha",
                new { CajaId = sesion.CajaId, Fecha = fecha.Date });

        if (sesion.CajeroId > 0)
            return (
                "pv.CAJERO_ID = @CajeroId AND pv.FECHA = @Fecha",
                new { CajeroId = sesion.CajeroId, Fecha = fecha.Date });

        throw new ArgumentException("No se pudo determinar la identidad del usuario para el resumen.");
    }

    /// <summary>
    /// Determina si un DOCTOS_PV fue creado por la sesion actual, usando la misma
    /// prioridad que <see cref="CondicionIdentidad"/>.
    /// </summary>
    private static bool EsVentaDeSesion(UsuarioSesion sesion, dynamic v)
    {
        if (!string.IsNullOrWhiteSpace(sesion.Usuario))
            return v.USUARIO_CREADOR?.ToString()?.Trim() == sesion.Usuario;

        int cajaId = Convert.ToInt32(v.CAJA_ID);
        int cajeroId = Convert.ToInt32(v.CAJERO_ID);

        if (sesion.CajaId > 0 && sesion.CajeroId > 0)
            return cajaId == sesion.CajaId && cajeroId == sesion.CajeroId;
        if (sesion.CajaId > 0)
            return cajaId == sesion.CajaId;
        if (sesion.CajeroId > 0)
            return cajeroId == sesion.CajeroId;
        return false;
    }

    public async Task<CierreJornadaDto> ObtenerResumenDiarioAsync(UsuarioSesion sesion, DateTime fecha)
    {
        using var connection = new FbConnection(_connectionString);

        // La identidad del usuario (usuario nativo / caja / cajero) viaja en los
        // claims del token JWT; ya no se confía en el vendedorId enviado por el cliente.
        var (condIdentidad, parametros) = CondicionIdentidad(sesion, fecha);

        // No Ventas ('N' in logic, mapped to 'V' with prefix 'NO VENTA:' to bypass DB constraint)
        var noVentasCount = await connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(DOCTO_PV_ID)
            FROM DOCTOS_PV pv
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' AND pv.DESCRIPCION LIKE 'NO VENTA:%'",
            parametros);

        // Ventas regulares ('V' sin prefijo 'NO VENTA:')
        var ventas = await connection.QueryAsync($@"
            SELECT pv.DOCTO_PV_ID, pv.TIPO_DOCTO,
                   (pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS) AS TOTAL
            FROM DOCTOS_PV pv
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' AND (pv.DESCRIPCION NOT LIKE 'NO VENTA:%' OR pv.DESCRIPCION IS NULL)",
            parametros);

        var listaVentas = ventas.ToList();
        var totalVentas = listaVentas.Sum(v => (decimal?)v.TOTAL ?? 0m);

        // Clientes Visitados (Distintos)
        var clientesVisitados = await connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(DISTINCT CLIENTE_ID)
            FROM DOCTOS_PV pv
            WHERE {condIdentidad}
              AND pv.ESTATUS = 'N'",
            parametros);

        int efectividad = clientesVisitados > 0 
            ? (int)Math.Round(((double)listaVentas.Count / clientesVisitados) * 100)
            : 0;

        // Devoluciones ('D')
        var devoluciones = await connection.QueryAsync($@"
            SELECT pv.DOCTO_PV_ID, pv.TIPO_DOCTO,
                   (pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS) AS TOTAL
            FROM DOCTOS_PV pv
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'D' AND pv.ESTATUS = 'N'",
            parametros);
            
        var listaDevoluciones = devoluciones.ToList();
        var totalDevoluciones = listaDevoluciones.Sum(d => (decimal?)d.TOTAL ?? 0m);

        // Cobros de Ventas ('V')
        var cobrosVentas = await connection.QueryAsync($@"
            SELECT fc.NOMBRE AS FORMA_COBRO, 
                   SUM(cob.IMPORTE) AS TOTAL_COBRADO
            FROM DOCTOS_PV_COBROS cob
            INNER JOIN DOCTOS_PV pv ON pv.DOCTO_PV_ID = cob.DOCTO_PV_ID
            INNER JOIN FORMAS_COBRO fc ON fc.FORMA_COBRO_ID = cob.FORMA_COBRO_ID
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N'
            GROUP BY fc.NOMBRE",
            parametros);

        var detalleCobros = cobrosVentas.Select(c => new ResumenFormaCobroDto
        {
            FormaCobro = c.FORMA_COBRO?.ToString()?.Trim() ?? "",
            TotalTickets = 0, // No agrupamos conteos exactos por ahora si no es vital
            TotalCobrado = c.TOTAL_COBRADO != null ? Convert.ToDecimal(c.TOTAL_COBRADO) : 0m
        }).ToList();

        // ================================================================
        // CLASIFICACION CONTADO vs CREDITO
        // ================================================================
        // Ya no usamos el calculo residual (total - cobrado).
        // Ahora clasificamos por FORMA_COBRO_ID directamente:
        //
        // FORMAS_COBRO con TIPO='R' se consideran CREDITO:
        //   71  = CREDITO
        //   703 = CREDITO 15D
        //   2205 = CREDITO 30D
        //
        // FORMAS_COBRO con TIPO='E','C','T','O' se consideran CONTADO:
        //   67   = EFECTIVO (E)
        //   68   = CHEQUE (C)
        //   2845 = TARJETA DEBITO (T)
        //   3702 = Transferencia (O)
        // ================================================================

        // Clasificar cobros de ventas por FORMA_COBRO
        // TIPO='R' = Credito, TIPO='E','C','T','O' = Contado
        var formasCobroVentas = await connection.QueryAsync($@"
            SELECT cob.FORMA_COBRO_ID, fc.TIPO, SUM(cob.IMPORTE) AS TOTAL
            FROM DOCTOS_PV_COBROS cob
            INNER JOIN DOCTOS_PV pv ON pv.DOCTO_PV_ID = cob.DOCTO_PV_ID
            INNER JOIN FORMAS_COBRO fc ON fc.FORMA_COBRO_ID = cob.FORMA_COBRO_ID
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N'
              AND (pv.DESCRIPCION NOT LIKE 'NO VENTA:%' OR pv.DESCRIPCION IS NULL)
            GROUP BY cob.FORMA_COBRO_ID, fc.TIPO",
            parametros);

        decimal ventaContado = 0m;
        decimal ventaCredito = 0m;

        foreach (var fc in formasCobroVentas)
        {
            string tipo = fc.TIPO?.ToString()?.Trim() ?? "";
            decimal total = fc.TOTAL != null ? Convert.ToDecimal(fc.TOTAL) : 0m;

            if (tipo == "R")
                ventaCredito += total;   // TIPO='R' = Credito
            else
                ventaContado += total;   // TIPO='E','C','T','O' = Contado
        }

        // Si hay diferencia entre totalVentas y la suma de cobros,
        // ajustamos credito (puede pasar si la venta no tiene cobros registrados)
        decimal sumaClasificada = ventaContado + ventaCredito;
        if (totalVentas > sumaClasificada && Math.Abs(totalVentas - sumaClasificada) > 0.01m)
        {
            ventaCredito += (totalVentas - sumaClasificada);
        }

        // ================================================================
        // COBRANZA: Documentos TIPO_DOCTO='P' (pagos recibidos)
        // ================================================================
        // Los documentos 'P' representan cobros de ventas a credito previas.
        // Se suman por separado ya que NO son ventas del dia, son pagos.
        var cobranza = await connection.QueryAsync($@"
            SELECT fc.NOMBRE AS FORMA_COBRO,
                   SUM(cob.IMPORTE) AS TOTAL_COBRADO
            FROM DOCTOS_PV_COBROS cob
            INNER JOIN DOCTOS_PV pv ON pv.DOCTO_PV_ID = cob.DOCTO_PV_ID
            INNER JOIN FORMAS_COBRO fc ON fc.FORMA_COBRO_ID = cob.FORMA_COBRO_ID
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'P' AND pv.ESTATUS = 'N'
            GROUP BY fc.NOMBRE",
            parametros);

        decimal cobranzaTotal = cobranza.Sum(c => (decimal?)c.TOTAL_COBRADO ?? 0m);
        decimal cobranzaEfectivo = cobranza
            .Where(c => c.FORMA_COBRO?.ToString()?.Contains("Efectivo", StringComparison.OrdinalIgnoreCase) == true)
            .Sum(c => (decimal?)c.TOTAL_COBRADO ?? 0m);

        // ================================================================
        // DEVOLUCIONES: Clasificar por FORMA_COBRO
        // ================================================================
        var formasCobroDev = await connection.QueryAsync($@"
            SELECT cob.FORMA_COBRO_ID, fc.TIPO, SUM(cob.IMPORTE) AS TOTAL
            FROM DOCTOS_PV_COBROS cob
            INNER JOIN DOCTOS_PV pv ON pv.DOCTO_PV_ID = cob.DOCTO_PV_ID
            INNER JOIN FORMAS_COBRO fc ON fc.FORMA_COBRO_ID = cob.FORMA_COBRO_ID
            WHERE {condIdentidad}
              AND pv.TIPO_DOCTO = 'D' AND pv.ESTATUS = 'N'
            GROUP BY cob.FORMA_COBRO_ID, fc.TIPO",
            parametros);

        decimal devolucionEfectivo = 0m;
        decimal devolucionCredito = 0m;

        foreach (var fd in formasCobroDev)
        {
            string tipo = fd.TIPO?.ToString()?.Trim() ?? "";
            decimal total = fd.TOTAL != null ? Convert.ToDecimal(fd.TOTAL) : 0m;

            if (tipo == "R")
                devolucionCredito += total;
            else
                devolucionEfectivo += total;
        }

        // Si hay devoluciones sin cobros registrados, se asignan a credito
        decimal sumaDevClasificada = devolucionEfectivo + devolucionCredito;
        if (totalDevoluciones > sumaDevClasificada && Math.Abs(totalDevoluciones - sumaDevClasificada) > 0.01m)
        {
            devolucionCredito += (totalDevoluciones - sumaDevClasificada);
        }

        // ================================================================
        // CALCULAR TOTALES PARA EL CIERRE
        // ================================================================
        // cobradoEfectivo: suma de cobros con FORMA_COBRO nombre "Efectivo"
        //   (se calcula desde detalleCobros que agrupa por nombre de forma)
        decimal cobradoEfectivo = detalleCobros
            .Where(c => c.FormaCobro.Contains("Efectivo", StringComparison.OrdinalIgnoreCase))
            .Sum(c => c.TotalCobrado);

        decimal totalCobradoVentas = detalleCobros.Sum(c => c.TotalCobrado);

        // Gastos (MVP = 0, se puede ampliar despues)
        decimal gastosOperativos = 0m;

        // Total Cierre: efectivo de ventas contado + cobranza efectivo
        // MENOS devoluciones en efectivo MENOS gastos operativos
        decimal totalCierre = cobradoEfectivo + cobranzaEfectivo - devolucionEfectivo - gastosOperativos;

        return new CierreJornadaDto
        {
            Fecha = fecha.Date,
            Ruta = string.IsNullOrWhiteSpace(sesion.VendedorNombre)
                ? $"VENDEDOR {sesion.VendedorId}"
                : sesion.VendedorNombre,
            
            ClientesVisitados = clientesVisitados,
            Efectividad = efectividad,
            NoVentas = noVentasCount,
            DevolucionesCount = listaDevoluciones.Count,

            TotalProductosSolicitados = 0,
            Preventas = 0,
            TotalPreventa = 0m,
            
            EntregasContado = 0m,
            EntregasCredito = 0m,
            
            Contado = ventaContado,
            Credito = ventaCredito,
            
            Cobrado = totalCobradoVentas + cobranzaTotal,
            CobradoEfectivo = cobradoEfectivo + cobranzaEfectivo,
            
            Devolucion = totalDevoluciones,
            DevolucionContado = devolucionEfectivo,
            DevolucionCredito = devolucionCredito,
            
            GastosOperativos = gastosOperativos,
            
            TotalCierre = totalCierre,
            
            DetalleCobros = detalleCobros
        };
    }

    // =========================================================================
    // ETAPA 3 - CIERRE: CERRAR RUTA
    // =========================================================================
    public async Task<RouteCloseResponseDto> CerrarRutaAsync(UsuarioSesion sesion, RouteCloseRequestDto request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (request.Fecha == default)
            throw new ArgumentException("La fecha de cierre es requerida.");

        // La identidad se resuelve del token JWT; el vendedorId del request ya no se usa.
        using var connection = new FbConnection(_connectionString);

        // Paso 1: Validar ventas (un solo query con IN)
        var ventasValidas = new List<int>();
        var ventasInvalidas = new List<int>();

        if (request.VentasRealizadas.Count > 0)
        {
            var ventasEncontradas = await connection.QueryAsync(@"
                SELECT DOCTO_PV_ID, CAJA_ID, CAJERO_ID, USUARIO_CREADOR, FECHA, ESTATUS
                FROM DOCTOS_PV
                WHERE DOCTO_PV_ID IN @Ids",
                new { Ids = request.VentasRealizadas });

            // Convertir dynamic a objetos tipados para evitar problemas
            // con LINQ sobre IEnumerable<dynamic> (no se puede pasar directamente a HashSet<int>)
            var idsValidos = new List<int>();
            foreach (var v in ventasEncontradas)
            {
                DateTime fechaBd = Convert.ToDateTime(v.FECHA);
                string estatus = v.ESTATUS?.ToString()?.Trim() ?? "";

                if (EsVentaDeSesion(sesion, v) &&
                    fechaBd.Date == request.Fecha.Date &&
                    estatus == "N")
                {
                    idsValidos.Add(Convert.ToInt32(v.DOCTO_PV_ID));
                }
            }
            var idsEncontrados = new HashSet<int>(idsValidos);

            foreach (var ventaId in request.VentasRealizadas)
            {
                if (idsEncontrados.Contains(ventaId))
                    ventasValidas.Add(ventaId);
                else
                    ventasInvalidas.Add(ventaId);
            }
        }

        // Paso 2: Resumen real del dia
        var resumenReal = await ObtenerResumenDiarioAsync(sesion, request.Fecha);

        // Paso 3: Calcular totales reportados
        decimal totalReportado = request.TotalEfectivo + request.TotalTarjeta + request.TotalCredito;

        // Paso 4: Calcular diferencias
        // La diferencia es el Total Reportado en efectivo menos el Total Cierre Teórico esperado
        decimal diferencia = request.TotalEfectivo - resumenReal.TotalCierre;

        return new RouteCloseResponseDto
        {
            Mensaje = ventasInvalidas.Count == 0
                ? "Cierre de ruta validado exitosamente"
                : $"Cierre completado con {ventasInvalidas.Count} ventas no encontradas",
            VendedorId = sesion.VendedorId,
            Fecha = request.Fecha,
            TotalVentasValidas = ventasValidas.Count,
            TotalVentasInvalidas = ventasInvalidas.Count,
            VentasInvalidas = ventasInvalidas,
            ResumenReal = resumenReal,
            TotalEfectivoReportado = request.TotalEfectivo,
            TotalTarjetaReportado = request.TotalTarjeta,
            TotalCreditoReportado = request.TotalCredito,
            TotalReportado = totalReportado,
            Diferencia = diferencia,
            HayDiferencia = Math.Abs(diferencia) > 0.01m,
            CombustibleGastado = request.CombustibleGastado,
            KilometrosRecorridos = request.KilometrosRecorridos,
            Observaciones = request.Observaciones
        };
    }

    // =========================================================================
    // DEBUG: Diagnostico y limpieza de DOCTOS_PV de prueba
    // =========================================================================
    public async Task<IEnumerable<dynamic>> ObtenerDebugDoctosPvAsync(int vendedorId, DateTime fecha)
    {
        using var connection = new FbConnection(_connectionString);
        return await connection.QueryAsync(@"
            SELECT pv.DOCTO_PV_ID, pv.TIPO_DOCTO, pv.FOLIO, pv.FECHA, pv.HORA,
                   pv.VENDEDOR_ID, pv.CLIENTE_ID, pv.ESTATUS, pv.APLICADO,
                   pv.IMPORTE_NETO, pv.TOTAL_IMPUESTOS, pv.DESCRIPCION,
                   c.NOMBRE AS CLIENTE_NOMBRE
            FROM DOCTOS_PV pv
            LEFT JOIN CLIENTES c ON c.CLIENTE_ID = pv.CLIENTE_ID
            WHERE pv.VENDEDOR_ID = @VendedorId AND pv.FECHA = @Fecha
            ORDER BY pv.HORA",
            new { VendedorId = vendedorId, Fecha = fecha.Date });
    }

    public async Task<int> EliminarDebugDoctosPvAsync(int vendedorId, DateTime fecha)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // Primero eliminar hijos
            await connection.ExecuteAsync(@"
                DELETE FROM IMPUESTOS_DOCTOS_PV WHERE DOCTO_PV_ID IN (
                    SELECT DOCTO_PV_ID FROM DOCTOS_PV WHERE VENDEDOR_ID = @VendedorId AND FECHA = @Fecha
                )",
                new { VendedorId = vendedorId, Fecha = fecha.Date }, transaction);

            await connection.ExecuteAsync(@"
                DELETE FROM DOCTOS_PV_COBROS WHERE DOCTO_PV_ID IN (
                    SELECT DOCTO_PV_ID FROM DOCTOS_PV WHERE VENDEDOR_ID = @VendedorId AND FECHA = @Fecha
                )",
                new { VendedorId = vendedorId, Fecha = fecha.Date }, transaction);

            await connection.ExecuteAsync(@"
                DELETE FROM DOCTOS_PV_DET WHERE DOCTO_PV_ID IN (
                    SELECT DOCTO_PV_ID FROM DOCTOS_PV WHERE VENDEDOR_ID = @VendedorId AND FECHA = @Fecha
                )",
                new { VendedorId = vendedorId, Fecha = fecha.Date }, transaction);

            // Luego la cabecera
            int deleted = await connection.ExecuteAsync(@"
                DELETE FROM DOCTOS_PV WHERE VENDEDOR_ID = @VendedorId AND FECHA = @Fecha",
                new { VendedorId = vendedorId, Fecha = fecha.Date }, transaction);

            await transaction.CommitAsync();
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

// ============================================================================
// INTERFAZ: IRouteService
// ============================================================================
public interface IRouteService
{
    /// <summary>
    /// Sincronizacion matutina. Recibe la identidad resuelta en el login
    /// (vendedor, caja, almacen, sucursal) via claims del token JWT.
    /// </summary>
    Task<SyncMorningRutaResponseDto> ObtenerSyncMatutinoAsync(UsuarioSesion sesion);
    Task<CierreJornadaDto> ObtenerResumenDiarioAsync(UsuarioSesion sesion, DateTime fecha);
    Task<RouteCloseResponseDto> CerrarRutaAsync(UsuarioSesion sesion, RouteCloseRequestDto request);
}

public interface IDebugRouteService
{
    Task<IEnumerable<dynamic>> ObtenerDebugDoctosPvAsync(int vendedorId, DateTime fecha);
    Task<int> EliminarDebugDoctosPvAsync(int vendedorId, DateTime fecha);
}
