// ============================================================================
// ARCHIVO: VentaServicePv.cs
// PROPOSITO: Servicio para registro de ventas en el modulo Punto de Venta (PV)
//            de Microsip ERP, sobre base Firebird CHOCOLATES.FDB
//
// DIFERENCIAS CON VentaService.cs (DOCTOS_VE):
//   VentaService.cs actual usa DOCTOS_VE (Ventas/Invoice, modulo Ventas)
//   VentaServicePv.cs usa DOCTOS_PV (Punto de Venta, modulo POS)
//
// TABLAS INVOLUCRADAS (Firebird):
//   DOCTOS_PV         -> Cabecera del ticket (71 columnas)
//   DOCTOS_PV_DET     -> Renglones del ticket (24 columnas)
//   DOCTOS_PV_COBROS  -> Formas de cobro (7 columnas)
//   IMPUESTOS_DOCTOS_PV -> Resumen de IVA (9 columnas)
//   FOLIOS_CAJAS      -> Folios por caja (4 columnas)
//   CLIENTES          -> Maestro de clientes
//   DIRS_CLIENTES     -> Direcciones de clientes
//   CAJAS             -> Puntos de venta
//   ARTICULOS         -> Maestro de articulos
//   FORMAS_COBRO      -> Catalogo de formas de cobro
//   IMPUESTOS         -> Catalogo de impuestos (IVA 16%=ID 622)
//
// TRIGGERS CRITICOS (NO LUCHAR CONTRA ELLOS):
//   DOCTOS_PV_BEFINS           -> Auto-genera DOCTO_PV_ID si llega -1
//   DOCTOS_PV_DET_BEFINS       -> Auto-genera DOCTO_PV_DET_ID y POSICION si llegan -1
//   DOCTOS_PV_COBROS_BEFINS    -> Auto-genera DOCTO_PV_COBRO_ID si llega -1
//   DOCTOS_PV_COBROS_AFTINS_0  -> Registra automaticamente en MOVTOS_EFVO_CAJA
// ============================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Servicio para registrar ventas en el modulo Punto de Venta (PV) de Microsip.
///
/// FLUJO COMPLETO DE REGISTRO DE VENTA PV:
///   0. Identidad (CAJA_ID, ALMACEN_ID, CAJERO_ID, SUCURSAL_ID, USUARIO_CREADOR)
///      se resuelve en el login nativo y viaja en los claims del token JWT.
///   1. Obtener MONEDA_ID, COND_PAGO_ID del cliente
///   2. Obtener DIR_CLI_ID del cliente
///   3. Validar CAJA_ID y ALMACEN_ID resueltos en el login
///   4. Obtener y generar FOLIO (FOLIOS_CAJAS, TIPO_DOCTO='V')
///   5. Calcular importes
///   6. INSERT cabecera DOCTOS_PV (trigger genera DOCTO_PV_ID)
///   7. INSERT detalle DOCTOS_PV_DET x cada articulo
///   8. INSERT impuesto IMPUESTOS_DOCTOS_PV (resumen IVA)
///   9. INSERT cobro DOCTOS_PV_COBROS (trigger registra en MOVTOS_EFVO_CAJA)
///  10. UPDATE FOLIOS_CAJAS (incrementar CONSECUTIVO)
///  11. COMMIT / ROLLBACK
/// </summary>
public class VentaServicePv : IVentaServicePv
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly IFolioService _folioService;
    private readonly IFolioLockService _folioLock;
    private readonly IFirebirdRetryPolicy _firebirdRetry;
    private readonly ILogger<VentaServicePv> _logger;

    public VentaServicePv(
        IConfiguration configuration,
        ILogger<VentaServicePv> logger,
        IFolioService? folioService = null,
        IFolioLockService? folioLock = null,
        IFirebirdRetryPolicy? firebirdRetry = null)
    {
        _configuration = configuration;
        _logger = logger;
        _folioService = folioService ?? new FolioService(configuration, NullLogger<FolioService>.Instance);
        _folioLock = folioLock ?? new FolioLockService(configuration, NullLogger<FolioLockService>.Instance);
        _firebirdRetry = firebirdRetry ?? new FirebirdRetryPolicy(configuration, NullLogger<FirebirdRetryPolicy>.Instance);
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
    }

    /// <summary>
    /// Registra una venta completa en DOCTOS_PV dentro de una transaccion.
    /// Los IDs se pasan como -1 para que los triggers BEFINS los generen.
    /// </summary>
    public async Task<VentaPvResponseDto> RegistrarVentaPvAsync(UsuarioSesion sesion, VentaPvCreateDto ventaDto)
    {
        if (ventaDto == null)
            throw new ArgumentNullException(nameof(ventaDto));

        if (ventaDto.Detalles == null || !ventaDto.Detalles.Any())
            throw new ArgumentException("La venta debe contener al menos un detalle.", nameof(ventaDto));

        // La identidad (caja, cajero, almacen, sucursal) viene del login
        // (claims del token JWT), nunca de la request.
        if (sesion == null || sesion.CajaId <= 0)
            throw new InvalidOperationException(
                "No se pudo resolver la caja del usuario. Verifica que el cajero tenga una caja asignada.");

        // El candado serializa las operaciones de la misma caja dentro del
        // proceso (elimina el deadlock en FOLIOS_CAJAS entre operaciones
        // concurrentes). El retry cubre los conflictos con otras instancias
        // o con Microsip emitiendo folios de la misma caja.
        await using var _ = await _folioLock.AcquireAsync(sesion.CajaId);
        return await _firebirdRetry.EjecutarAsync(() => EjecutarVentaCoreAsync(sesion, ventaDto));
    }

    private async Task<VentaPvResponseDto> EjecutarVentaCoreAsync(UsuarioSesion sesion, VentaPvCreateDto ventaDto)
    {
        // Valores por defecto desde appsettings
        int defaultMonedaId = _configuration.GetValue<int>("MicrosipSettings:DefaultMonedaId", 1);
        int defaultCondPagoId = _configuration.GetValue<int>("MicrosipSettings:DefaultCondPagoId", 1);
        int defaultImpuestoId = _configuration.GetValue<int?>("MicrosipSettings:DefaultImpuestoId") ?? 622;

        int cajaId = sesion.CajaId;
        int almacenId = sesion.AlmacenId > 0 ? sesion.AlmacenId
            : _configuration.GetValue<int?>("MicrosipSettings:DefaultAlmacenId") ?? 19;
        int sucursalId = sesion.SucursalId > 0 ? sesion.SucursalId
            : _configuration.GetValue<int?>("MicrosipSettings:DefaultSucursalId") ?? 4274;
        int cajeroId = sesion.CajeroId > 0 ? sesion.CajeroId
            : _configuration.GetValue<int>("MicrosipSettings:DefaultCajeroId", 2419);
        string usuarioCreador = string.IsNullOrEmpty(sesion.Usuario)
            ? "MOVIL" : sesion.Usuario;

        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // PASO 1: Obtener datos del cliente (MONEDA_ID, COND_PAGO_ID)
            var clientInfo = await connection.QueryFirstOrDefaultAsync(@"
                SELECT c.MONEDA_ID, c.COND_PAGO_ID
                FROM CLIENTES c
                WHERE c.CLIENTE_ID = @ClienteId AND c.ESTATUS = 'A'",
                new { ClienteId = ventaDto.ClienteId },
                transaction: transaction);

            int monedaId = defaultMonedaId;
            int condPagoId = defaultCondPagoId;
            if (clientInfo != null)
            {
                monedaId = clientInfo.MONEDA_ID != null ? Convert.ToInt32(clientInfo.MONEDA_ID) : defaultMonedaId;
                condPagoId = clientInfo.COND_PAGO_ID != null ? Convert.ToInt32(clientInfo.COND_PAGO_ID) : defaultCondPagoId;
            }

            // PASO 2: Obtener direccion del cliente
            var dirCliId = await connection.QueryFirstOrDefaultAsync<int?>(@"
                SELECT FIRST 1 DIR_CLI_ID FROM DIRS_CLIENTES WHERE CLIENTE_ID = @ClienteId",
                new { ClienteId = ventaDto.ClienteId },
                transaction: transaction);

            if (dirCliId == null)
            {
                dirCliId = await connection.QueryFirstOrDefaultAsync<int?>(@"
                    SELECT FIRST 1 DIR_CLI_ID FROM DIRS_CLIENTES",
                    transaction: transaction);
                if (dirCliId == null)
                    throw new InvalidOperationException(
                        $"El cliente con ID {ventaDto.ClienteId} no tiene direcciones registradas.");
            }

            // PASO 3: Validar que la caja resuelta en el login exista en CAJAS
            var cajaAlmacen = await connection.QueryFirstOrDefaultAsync<int?>(@"
                SELECT ALMACEN_ID FROM CAJAS WHERE CAJA_ID = @CajaId",
                new { CajaId = cajaId }, transaction: transaction);
            if (cajaAlmacen == null)
                throw new InvalidOperationException($"La caja con ID {cajaId} no existe.");
            if (sesion.AlmacenId > 0)
                almacenId = cajaAlmacen.Value;

            // PASO 4: Reserva atomica de folio (FOLIOS_CAJAS, TIPO_DOCTO='V')
            // Elimina el SELECT-then-UPDATE que causaba isc_deadlock: un solo
            // UPDATE ... RETURNING reserva el siguiente folio de forma segura.
            string folio = await _folioService.ReservarFolioAsync(connection, transaction, cajaId, "V");

            // PASO 6: USUARIO_CREADOR = usuario nativo de Microsip (del login)
            // Ya no se resuelve desde AGENTES; viene en los claims del token.

            // PASO 7: Batch-load all distinct tax rates to avoid N+1
            // (incluye los impuestos COMPUESTOS de la lista 'Impuestos' del detalle)
            var impuestosUsados = ventaDto.Detalles
                .SelectMany(d => ObtenerImpuestosDetalle(d, defaultImpuestoId))
                .Distinct().ToList();

            // Tasas enviadas por el movil como fallback cuando el impuesto no esta en el catalogo
            var tasasCliente = new Dictionary<int, decimal>();
            foreach (var d in ventaDto.Detalles)
            {
                if (d.Impuestos == null) continue;
                foreach (var t in d.Impuestos)
                {
                    if (t.ImpuestoId > 0 && t.PctjeImpuesto > 0)
                        tasasCliente[t.ImpuestoId] = t.PctjeImpuesto / 100m;
                }
            }

            var tasasImpuestos = new Dictionary<int, decimal>();
            foreach (var impId in impuestosUsados)
            {
                var info = await connection.QueryFirstOrDefaultAsync(@"
                    SELECT PCTJE_IMPUESTO FROM IMPUESTOS WHERE IMPUESTO_ID = @ImpuestoId",
                    new { ImpuestoId = impId }, transaction: transaction);
                if (info?.PCTJE_IMPUESTO != null)
                {
                    tasasImpuestos[impId] = Convert.ToDecimal(info.PCTJE_IMPUESTO) / 100m;
                }
                else if (tasasCliente.TryGetValue(impId, out var tasaCliente))
                {
                    tasasImpuestos[impId] = tasaCliente;
                }
                else
                {
                    tasasImpuestos[impId] = 0.16m;
                }
            }

            // PASO 8: Calcular importes totales
            decimal importeNeto = ventaDto.Detalles.Sum(d => d.Unidades * d.PrecioUnitario);
            decimal totalImpuestos = ventaDto.Detalles.Sum(d =>
            {
                decimal factor = FactorImpuestos(d, tasasImpuestos, defaultImpuestoId);
                return d.Unidades * d.PrecioUnitario * (factor - 1m);
            });

            string? descripcion = string.IsNullOrEmpty(ventaDto.Notas) ? null :
                (ventaDto.Notas.Length > 200 ? ventaDto.Notas.Substring(0, 200) : ventaDto.Notas);

            // PASO 9: INSERT cabecera DOCTOS_PV (trigger DOCTOS_PV_BEFINS genera ID)
            // Cajero resuelto en el login (claims del token).
            var timeOfDay = ventaDto.FechaHora.TimeOfDay;
            _logger.LogInformation("[PV] Cajero {CajeroId}, caja {CajaId}, almacen {AlmacenId} desde claims", cajeroId, cajaId, almacenId);
            string insertHeaderSql = @"
                INSERT INTO DOCTOS_PV (
                    DOCTO_PV_ID, CAJA_ID, TIPO_DOCTO, SUCURSAL_ID, FOLIO,
                    FECHA, HORA, CAJERO_ID, CLIENTE_ID, DIR_CLI_ID,
                    ALMACEN_ID, MONEDA_ID, IMPUESTO_INCLUIDO, TIPO_CAMBIO,
                    ESTATUS, APLICADO, PROCESO_ORIGEN, SISTEMA_ORIGEN, VENDEDOR_ID,
                    IMPORTE_NETO, TOTAL_IMPUESTOS, TOTAL_RETENCIONES,
                    PESO_EMBARQUE, DESCRIPCION, ES_CFD, ENVIADO,
                    CFDI_CERTIFICADO, CARGAR_SUN, USUARIO_CREADOR,
                    FECHA_HORA_CREACION, PARTIDA_AJUSTE_ID, PRECIO_ORIG_PARTIDA_AJUSTE
                ) VALUES (
                    -1, @CajaId, 'V', @SucursalId, @Folio,
                    @Fecha, @Hora, @CajeroId, @ClienteId, @DirCliId,
                    @AlmacenId, @MonedaId, 'S', 1.0,
                    'N', 'N', 'N', 'PV', @VendedorId,
                    @ImporteNeto, @TotalImpuestos, 0.0,
                    0.0, @Descripcion, 'N', 'N',
                    'N', 'N', @UsuarioCreador,
                    @FechaHoraCreacion, 0, 0.0
                ) RETURNING DOCTO_PV_ID;";

            int generatedDoctoPvId = await connection.ExecuteScalarAsync<int>(
                insertHeaderSql,
                new
                {
                    CajaId = cajaId,
                    SucursalId = sucursalId,
                    Folio = folio,
                    Fecha = ventaDto.FechaHora.Date,
                    Hora = new TimeSpan(timeOfDay.Hours, timeOfDay.Minutes, timeOfDay.Seconds),
                    CajeroId = cajeroId,
                    ClienteId = ventaDto.ClienteId,
                    DirCliId = dirCliId.Value,
                    AlmacenId = almacenId,
                    MonedaId = monedaId,
                    VendedorId = ventaDto.VendedorId,
                    ImporteNeto = importeNeto,
                    TotalImpuestos = totalImpuestos,
                    Descripcion = descripcion,
                    UsuarioCreador = usuarioCreador,
                    FechaHoraCreacion = ventaDto.FechaHora
                },
                transaction: transaction);

            // PASO 9: INSERT detalle DOCTOS_PV_DET (trigger genera ID y POSICION)
            //         + IMPUESTOS_DOCTOS_PV_DET por cada renglon
            string insertDetailSql = @"
                INSERT INTO DOCTOS_PV_DET (
                    DOCTO_PV_DET_ID, DOCTO_PV_ID, ARTICULO_ID, UNIDADES,
                    PRECIO_UNITARIO, PRECIO_UNITARIO_IMPTO, IMPUESTO_POR_UNIDAD,
                    PRECIO_TOTAL_NETO, PRECIO_MODIFICADO, ROL, POSICION
                ) VALUES (
                    -1, @DoctoPvId, @ArticuloId, @Unidades,
                    @PrecioUnitario, @PrecioUnitarioImp, @ImpuestoUnidad,
                    @TotalRenglon, 'N', 'N', -1
                ) RETURNING DOCTO_PV_DET_ID;";

            string insertTaxDetSql = @"
                INSERT INTO IMPUESTOS_DOCTOS_PV_DET (
                    DOCTO_PV_DET_ID, IMPUESTO_ID, DOCTO_PV_ID,
                    ID_INTERNO_TIPO_IMPTO, TIPO_CALC,
                    IMPORTE_IMPUESTO_BRUTO, VENTA_NETA, VENTA_BRUTA,
                    OTROS_IMPUESTOS, PCTJE_IMPUESTO, IMPORTE_IMPUESTO,
                    UNIDADES_IMPUESTO, IMPORTE_UNITARIO_IMPUESTO
                ) VALUES (
                    @DoctoPvDetId, @ImpuestoId, @DoctoPvId,
                    'V', 'P',
                    @ImporteImpuesto, @VentaNeta, @VentaNeta,
                    0.0, @PctjeImpuesto, @ImporteImpuesto,
                    0, 0
                );";

            int? primerDoctoPvDetId = null;

            foreach (var detail in ventaDto.Detalles)
            {
                var impuestosDetalle = ObtenerImpuestosDetalle(detail, defaultImpuestoId);

                // Factor COMPUESTO: ∏(1 + pctje/100), ej: 1.16 * 1.03 = 1.1948
                decimal factor = 1m;
                foreach (var impId in impuestosDetalle)
                    factor *= 1m + tasasImpuestos[impId];

                decimal precioUnitario = detail.PrecioUnitario;
                decimal unidades = detail.Unidades;
                decimal precioUnitarioImp = precioUnitario * factor;
                decimal impuestoUnidad = precioUnitario * (factor - 1m);
                decimal totalRenglon = unidades * precioUnitario;

                int doctoPvDetId = await connection.ExecuteScalarAsync<int>(
                    insertDetailSql, new
                    {
                        DoctoPvId = generatedDoctoPvId,
                        ArticuloId = detail.ArticuloId,
                        Unidades = unidades,
                        PrecioUnitario = precioUnitario,
                        PrecioUnitarioImp = precioUnitarioImp,
                        ImpuestoUnidad = impuestoUnidad,
                        TotalRenglon = totalRenglon
                    }, transaction: transaction);

                if (primerDoctoPvDetId == null)
                    primerDoctoPvDetId = doctoPvDetId;

                // Un renglon de IMPUESTOS_DOCTOS_PV_DET por CADA impuesto compuesto
                foreach (var impId in impuestosDetalle)
                {
                    decimal tasaImpuesto = tasasImpuestos[impId];
                    decimal importeImpuestoDet = totalRenglon * tasaImpuesto;
                    decimal pctjeImpuestoDet = tasaImpuesto * 100m;

                    await connection.ExecuteAsync(insertTaxDetSql, new
                    {
                        DoctoPvDetId = doctoPvDetId,
                        ImpuestoId = impId,
                        DoctoPvId = generatedDoctoPvId,
                        ImporteImpuesto = importeImpuestoDet,
                        VentaNeta = totalRenglon,
                        PctjeImpuesto = pctjeImpuestoDet
                    }, transaction: transaction);
                }
            }

            // Actualizar PARTIDA_AJUSTE_ID con el ID del primer detalle
            if (primerDoctoPvDetId != null)
            {
                await connection.ExecuteAsync(@"
                    UPDATE DOCTOS_PV SET PARTIDA_AJUSTE_ID = @DetId,
                        PRECIO_ORIG_PARTIDA_AJUSTE = IMPORTE_NETO
                    WHERE DOCTO_PV_ID = @DoctoPvId",
                    new { DetId = primerDoctoPvDetId.Value, DoctoPvId = generatedDoctoPvId },
                    transaction: transaction);
            }

            // PASO 10: INSERT resumen de impuestos IMPUESTOS_DOCTOS_PV
            string insertTaxSql = @"
                INSERT INTO IMPUESTOS_DOCTOS_PV (
                    DOCTO_PV_ID, IMPUESTO_ID, VENTA_NETA, VENTA_BRUTA,
                    OTROS_IMPUESTOS, PCTJE_IMPUESTO, IMPORTE_IMPUESTO,
                    UNIDADES_IMPUESTO, IMPORTE_UNITARIO_IMPUESTO
                ) VALUES (
                    @DoctoPvId, @ImpuestoId, @VentaNeta, @VentaBruta,
                    0.0, @PctjeImpuesto, @ImporteImpuesto, 0, 0
                );";

            // Resumen por impuesto (agrupando TODOS los compuestos del detalle)
            var resumenImpuestos = new Dictionary<int, (decimal BaseGravable, decimal Importe)>();
            foreach (var detalle in ventaDto.Detalles)
            {
                var impuestosDetalle = ObtenerImpuestosDetalle(detalle, defaultImpuestoId);
                decimal baseDetalle = detalle.Unidades * detalle.PrecioUnitario;
                foreach (var impId in impuestosDetalle)
                {
                    if (!resumenImpuestos.TryGetValue(impId, out var grupo))
                    {
                        grupo = (0m, 0m);
                    }
                    resumenImpuestos[impId] = (
                        grupo.BaseGravable + baseDetalle,
                        grupo.Importe + baseDetalle * tasasImpuestos[impId]);
                }
            }

            foreach (var grupo in resumenImpuestos)
            {
                await connection.ExecuteAsync(insertTaxSql, new
                {
                    DoctoPvId = generatedDoctoPvId,
                    ImpuestoId = grupo.Key,
                    VentaNeta = grupo.Value.BaseGravable,
                    VentaBruta = grupo.Value.BaseGravable,
                    PctjeImpuesto = tasasImpuestos[grupo.Key] * 100m,
                    ImporteImpuesto = grupo.Value.Importe
                }, transaction: transaction);
            }

            // ================================================================
            // Importe del cobro a registrar en DOCTOS_PV_COBROS.
            // Debe coincidir EXACTAMENTE con el saldo del cargo que generara
            // el procedimiento GENERA_DOCTO_CC_PV en CxC, el cual se trunca a
            // 2 decimales (IMPORTES_DOCTOS_CC.IMPORTE + IMPUESTO).
            // Si el abono excede el saldo del cargo (aunque sea por 1 centavo),
            // el trigger IMPTES_DOCTOS_CC_BEFUPD_0 lanza EX_SALDO_CARGO_EXCEDIDO
            // y toda la venta se revierte. Ej: neto 54.929697 + imp 8.788752
            // -> cargo 54.92 + 8.78 = 63.70, pero total 63.718449 -> 63.71 > 63.70.
            decimal totalDocumento = importeNeto + totalImpuestos;
            decimal cobroImporte = Math.Truncate(importeNeto * 100m) / 100m
                                 + Math.Truncate(totalImpuestos * 100m) / 100m;

            // PASO 11: INSERT cobros en DOCTOS_PV_COBROS
            // ================================================================
            // Soporta DOS modos:
            //   MODO 1 (nuevo): Pagos tiene datos -> inserta cada forma de cobro
            //   MODO 2 (compatible): Pagos vacio -> usa FormaCobroId individual
            //
            // DOCTOS_PV_COBROS.TIPO:
            //   'C' = Complete (pago completo en efectivo/tarjeta)
            //   'P' = Partial (porcion a credito en pago mixto)
            //   'A' = Advance (anticipo)
            //
            // TRIGGER ASOCIADO:
            //   DOCTOS_PV_COBROS_AFTINS_0 -> Registra automaticamente
            //   en MOVTOS_EFVO_CAJA para formas de cobro en efectivo.
            //
            // RELACION CON CxC (via GENERA_DOCTO_CC_PV en el trigger):
            //   TIPO='C' en efectivo -> Crea abono en DOCTOS_CC
            //   TIPO='P' en credito -> NO crea abono, solo el cargo
            //   FORMA_COBRO_ID=71 -> Marca como credito en SALDOS_CC
            // ================================================================
            string insertPaymentSql = @"
                INSERT INTO DOCTOS_PV_COBROS (
                    DOCTO_PV_COBRO_ID, DOCTO_PV_ID, TIPO, FORMA_COBRO_ID,
                    IMPORTE, TIPO_CAMBIO, IMPORTE_MON_DOC
                ) VALUES (
                    -1, @DoctoPvId, @Tipo, @FormaCobroId,
                    @Importe, 1.0, @Importe
                );";

            // Determinar formas de cobro: priorizar Pagos, fallback a FormaCobroId
            var cobrosAInsertar = new List<(int formaCobroId, decimal importe)>();

            // IDs de formas de cobro que representan CREDITO (TIPO='R' en FORMAS_COBRO)
            var idsCredito = _configuration
                .GetSection("MicrosipSettings:CreditFormaCobroIds")
                .Get<int[]>() ?? [71, 703, 2205];
            var creditSet = new HashSet<int>(idsCredito);

            if (ventaDto.Pagos != null && ventaDto.Pagos.Count > 0)
            {
                // MODO 1: Usar la lista de pagos (soporta pagos mixtos)
                foreach (var pago in ventaDto.Pagos)
                {
                    if (pago.Importe > 0)
                        cobrosAInsertar.Add((pago.FormaCobroId, pago.Importe));
                }
            }
            else
            {
                // MODO 2: Usar la propiedad individual (compatibilidad hacia atras)
                int formaCobroId = ventaDto.FormaCobroId > 0
                    ? ventaDto.FormaCobroId
                    : _configuration.GetValue<int>("MicrosipSettings:DefaultFormaCobroId", 67);
                cobrosAInsertar.Add((formaCobroId, cobroImporte));
            }

            // ================================================================
            // PASO 11b: VALIDAR LIMITE DE CREDITO
            // ================================================================
            // Si la venta incluye una porcion a credito (forma de cobro en
            // creditSet), se valida contra el limite del cliente:
            //   saldo_pendiente (SALDOS_CC historico) + credito_nuevo <= LIMITE
            // Si se excede, la venta se RECHAZA con el monto del exceso
            // (el controller responde 400 y la app muestra el mensaje).
            // LIMITE_CREDITO <= 0 se interpreta como sin restriccion.
            // ================================================================
            decimal montoCredito = cobrosAInsertar
                .Where(c => creditSet.Contains(c.formaCobroId))
                .Sum(c => c.importe);

            if (montoCredito > 0m)
            {
                decimal limiteCredito = await connection.QueryFirstOrDefaultAsync<decimal>(@"
                    SELECT COALESCE(LIMITE_CREDITO, 0)
                    FROM CLIENTES WHERE CLIENTE_ID = @ClienteId",
                    new { ClienteId = ventaDto.ClienteId },
                    transaction: transaction);

                if (limiteCredito > 0m)
                {
                    decimal saldoPendiente = await connection.QueryFirstOrDefaultAsync<decimal>(@"
                        SELECT COALESCE(SUM(CARGOS_CXC), 0) - COALESCE(SUM(CREDITOS_CXC), 0)
                        FROM SALDOS_CC WHERE CLIENTE_ID = @ClienteId",
                        new { ClienteId = ventaDto.ClienteId },
                        transaction: transaction);

                    decimal totalProyectado = saldoPendiente + montoCredito;
                    if (totalProyectado > limiteCredito)
                    {
                        decimal exceso = totalProyectado - limiteCredito;
                        throw new InvalidOperationException(
                            $"Límite de crédito sobrepasado por ${exceso:0.00}. " +
                            $"Saldo actual ${saldoPendiente:0.00} + compra a crédito ${montoCredito:0.00} = ${totalProyectado:0.00}, " +
                            $"límite ${limiteCredito:0.00}. La venta no se registró.");
                    }
                }
            }

            // Insertar cada forma de cobro como un registro en DOCTOS_PV_COBROS
            foreach (var (formaId, importe) in cobrosAInsertar)
            {
                // TIPO='P' para creditos, 'C' para contado/otros
                string tipoCobro = creditSet.Contains(formaId) ? "P" : "C";

                await connection.ExecuteAsync(insertPaymentSql, new
                {
                    DoctoPvId = generatedDoctoPvId,
                    Tipo = tipoCobro,
                    FormaCobroId = formaId,
                    Importe = importe
                }, transaction: transaction);
            }

            // ================================================================
            // PASO 12: AUTO-APLICAR la venta (APLICADO = 'S')
            // ================================================================
            // Dispara el trigger DOCTOS_PV_AFTUPD_0 -> APLICA_DOCTO_PV -> APLICA_VTA_PV
            // que afecta inventarios, genera CxC y hace visible la venta
            // en el Punto de Venta de Microsip.
            //
            // Configurable: MicrosipSettings:AplicarVentaAutomatica (default true)
            // ================================================================
            bool aplicarAutomaticamente = _configuration.GetValue<bool>(
                "MicrosipSettings:AplicarVentaAutomatica", true);

            if (aplicarAutomaticamente)
            {
                int filasAplicadas = await connection.ExecuteAsync(@"
                    UPDATE DOCTOS_PV SET APLICADO = 'S', FECHA_VIGENCIA = CURRENT_DATE
                    WHERE DOCTO_PV_ID = @DoctoPvId AND APLICADO = 'N' AND ESTATUS = 'N'",
                    new { DoctoPvId = generatedDoctoPvId },
                    transaction: transaction);

                if (filasAplicadas > 0)
                    _logger.LogInformation("[PV] Venta {DoctoPvId} aplicada automaticamente (APLICADO='S')", generatedDoctoPvId);
                else
                    _logger.LogWarning("[PV] No se pudo auto-aplicar la venta {DoctoPvId}", generatedDoctoPvId);
            }

            await transaction.CommitAsync();

            return new VentaPvResponseDto
            {
                Message = "Venta registrada exitosamente en Punto de Venta",
                VentaMovilId = ventaDto.VentaMovilId,
                DoctoPvId = generatedDoctoPvId,
                Folio = folio,
                TotalRenglones = ventaDto.Detalles.Count,
                ImporteNeto = importeNeto,
                TotalImpuestos = totalImpuestos,
                TotalDocumento = totalDocumento
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "[FATAL PV] Error al registrar venta en DOCTOS_PV: VentaMovilId={VentaMovilId}, VendedorId={VendedorId}, ClienteId={ClienteId}, FormaCobroId={FormaCobroId}, CajaId={CajaId}, Detalles={Detalles}",
                ventaDto.VentaMovilId, ventaDto.VendedorId, ventaDto.ClienteId, ventaDto.FormaCobroId, ventaDto.CajaId, ventaDto.Detalles?.Count ?? 0);
            throw;
        }
    }

    /// <summary>
    /// IDs de impuestos a aplicar en un detalle.
    /// Si el detalle trae la lista compuesta 'Impuestos', se usan TODOS;
    /// si no, se usa el impuesto principal [ImpuestoId] (compatibilidad).
    /// </summary>
    private static List<int> ObtenerImpuestosDetalle(DetalleVentaPvDto d, int defaultImpuestoId)
    {
        if (d.Impuestos != null && d.Impuestos.Count > 0)
        {
            var ids = d.Impuestos
                .Where(t => t.ImpuestoId > 0)
                .Select(t => t.ImpuestoId)
                .ToList();
            if (ids.Count > 0)
                return ids;
        }
        return new List<int> { d.ImpuestoId > 0 ? d.ImpuestoId : defaultImpuestoId };
    }

    /// <summary>
    /// Factor de impuestos COMPUESTO de un detalle: ∏(1 + pctje/100).
    /// Ej: IVA 16% + IEPS 3% -> 1.16 * 1.03 = 1.1948.
    /// </summary>
    private static decimal FactorImpuestos(DetalleVentaPvDto d, IReadOnlyDictionary<int, decimal> tasas, int defaultImpuestoId)
    {
        decimal factor = 1m;
        foreach (var impId in ObtenerImpuestosDetalle(d, defaultImpuestoId))
            factor *= 1m + tasas[impId];
        return factor;
    }

    /// <summary>
    /// Aplica la venta (afecta inventarios y CxC).
    /// Dispara trigger DOCTOS_PV_AFTUPD_0 que ejecuta APLICA_DOCTO_PV.
    /// </summary>
    public async Task<bool> AplicarVentaAsync(int doctoPvId)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        var estado = await connection.QueryFirstOrDefaultAsync(@"
            SELECT APLICADO, ESTATUS FROM DOCTOS_PV WHERE DOCTO_PV_ID = @DoctoPvId",
            new { DoctoPvId = doctoPvId });

        if (estado == null)
            throw new InvalidOperationException($"El documento {doctoPvId} no existe.");
        if (estado.APLICADO == "S")
            throw new InvalidOperationException($"El documento {doctoPvId} ya esta aplicado.");
        if (estado.ESTATUS == "C")
            throw new InvalidOperationException($"El documento {doctoPvId} esta cancelado.");

        int rows = await connection.ExecuteAsync(@"
            UPDATE DOCTOS_PV SET APLICADO = 'S', FECHA_VIGENCIA = CURRENT_DATE
            WHERE DOCTO_PV_ID = @DoctoPvId AND APLICADO = 'N' AND ESTATUS = 'N'",
            new { DoctoPvId = doctoPvId });

        return rows > 0;
    }

    /// <summary>
    /// Cancela la venta. Dispara trigger DOCTOS_PV_BEFUPD_0 que valida y
    /// ejecuta DESAPLICA_DOCTO_PV (revierte inventarios y CxC).
    /// </summary>
    public async Task<bool> CancelarVentaAsync(int doctoPvId)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        int rows = await connection.ExecuteAsync(@"
            UPDATE DOCTOS_PV SET ESTATUS = 'C'
            WHERE DOCTO_PV_ID = @DoctoPvId AND ESTATUS = 'N'",
            new { DoctoPvId = doctoPvId });

        return rows > 0;
    }

    /// <summary>
    /// Consulta un ticket completo con JOIN de 5+ tablas.
    /// </summary>
    public async Task<IEnumerable<dynamic>> ConsultarTicketAsync(int doctoPvId)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = @"
            SELECT
                pv.DOCTO_PV_ID, pv.FOLIO, pv.FECHA, pv.HORA,
                pv.TIPO_DOCTO, pv.ESTATUS, pv.APLICADO,
                pv.IMPORTE_NETO, pv.TOTAL_IMPUESTOS,
                (pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS) AS TOTAL_DOCUMENTO,
                c.NOMBRE AS CLIENTE, v.NOMBRE AS VENDEDOR,
                cj.NOMBRE AS CAJA, det.POSICION,
                a.NOMBRE AS ARTICULO, det.UNIDADES,
                det.PRECIO_UNITARIO, det.PRECIO_UNITARIO_IMPTO,
                det.PRECIO_TOTAL_NETO AS SUBTOTAL_RENGLON,
                fc.NOMBRE AS FORMA_COBRO, cob.IMPORTE AS MONTO_COBRADO
            FROM DOCTOS_PV pv
            INNER JOIN DOCTOS_PV_DET det ON det.DOCTO_PV_ID = pv.DOCTO_PV_ID
            INNER JOIN ARTICULOS a ON a.ARTICULO_ID = det.ARTICULO_ID
            LEFT JOIN CLIENTES c ON c.CLIENTE_ID = pv.CLIENTE_ID
            LEFT JOIN VENDEDORES v ON v.VENDEDOR_ID = pv.VENDEDOR_ID
            LEFT JOIN CAJAS cj ON cj.CAJA_ID = pv.CAJA_ID
            LEFT JOIN DOCTOS_PV_COBROS cob ON cob.DOCTO_PV_ID = pv.DOCTO_PV_ID
            LEFT JOIN FORMAS_COBRO fc ON fc.FORMA_COBRO_ID = cob.FORMA_COBRO_ID
            WHERE pv.DOCTO_PV_ID = @DoctoPvId
            ORDER BY det.POSICION";

        return await connection.QueryAsync(sql, new { DoctoPvId = doctoPvId });
    }

    public async Task<(int DoctoPvId, string Folio)> RegistrarNoVentaPvAsync(UsuarioSesion sesion, NoVentaPvCreateDto dto)
    {
        // La identidad (caja, cajero, almacen) viene del login (claims JWT)
        if (sesion == null || sesion.CajaId <= 0)
            throw new InvalidOperationException(
                "No se pudo resolver la caja del usuario. Verifica que el cajero tenga una caja asignada.");

        await using var _ = await _folioLock.AcquireAsync(sesion.CajaId);
        return await _firebirdRetry.EjecutarAsync(() => EjecutarNoVentaCoreAsync(sesion, dto));
    }

    private async Task<(int DoctoPvId, string Folio)> EjecutarNoVentaCoreAsync(UsuarioSesion sesion, NoVentaPvCreateDto dto)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            int cajaId = sesion.CajaId;
            int almacenId = sesion.AlmacenId > 0 ? sesion.AlmacenId
                : _configuration.GetValue<int>("MicrosipSettings:DefaultAlmacenId", 19);
            int cajeroId = sesion.CajeroId > 0 ? sesion.CajeroId
                : _configuration.GetValue<int>("MicrosipSettings:DefaultCajeroId", 2419);
            string usuarioCreador = string.IsNullOrEmpty(sesion.Usuario)
                ? "MOVIL" : sesion.Usuario;

            // PASO 1: Obtener DirCliId del cliente
            var dirCliId = await connection.QueryFirstOrDefaultAsync<int?>(@"
                SELECT FIRST 1 DIR_CLI_ID FROM DIRS_CLIENTES
                WHERE CLIENTE_ID = @ClienteId",
                new { ClienteId = dto.ClienteId }, transaction: transaction);

            // Fallback: si el cliente no tiene direccion propia, usar cualquier DIR_CLI_ID disponible
            // (mismo comportamiento que RegistrarVentaPvAsync para evitar fallos por datos incompletos)
            if (dirCliId == null || dirCliId == 0)
            {
                dirCliId = await connection.QueryFirstOrDefaultAsync<int?>(@"
                    SELECT FIRST 1 DIR_CLI_ID FROM DIRS_CLIENTES",
                    transaction: transaction);
                if (dirCliId == null || dirCliId == 0)
                    throw new InvalidOperationException(
                        $"No se encontro ninguna direccion en DIRS_CLIENTES para la no-venta del cliente {dto.ClienteId}.");
            }

            // PASO 2: Validar que la caja resuelta en el login exista en CAJAS
            var cajaAlmacen = await connection.QueryFirstOrDefaultAsync<int?>(
                "SELECT ALMACEN_ID FROM CAJAS WHERE CAJA_ID = @CajaId",
                new { CajaId = cajaId }, transaction: transaction);
            if (cajaAlmacen != null && sesion.AlmacenId > 0)
                almacenId = cajaAlmacen.Value;

            // PASO 3: Reserva atomica de folio para 'V' (misma logica segura
            // que en ventas: UPDATE ... RETURNING, sin SELECT-then-UPDATE).
            string folio = await _folioService.ReservarFolioAsync(connection, transaction, cajaId, "V");

            int sucursalId = sesion.SucursalId > 0 ? sesion.SucursalId
                : _configuration.GetValue<int>("MicrosipSettings:DefaultSucursalId", 4274);
            int monedaId = _configuration.GetValue<int>("MicrosipSettings:DefaultMonedaId", 1);

            // DOCTOS_PV.DESCRIPCION contiene causa y comentario (sin FOTO: — la foto vive en BD/C).
            string desc = $"NO VENTA: CAUSA: {dto.CausaDesc}. NOTAS: {dto.Comentario ?? "N/A"}.";
            if (desc.Length > 200) desc = desc.Substring(0, 200);

            var timeOfDay = dto.FechaHora.TimeOfDay;

            // Insert DOCTOS_PV con TIPO_DOCTO = 'V' para evadir la restriccion (CHECK DOCTOS_PV_TIPO_DOCTO_CHK)
            string insertHeaderSql = @"
                INSERT INTO DOCTOS_PV (
                    DOCTO_PV_ID, CAJA_ID, TIPO_DOCTO, SUCURSAL_ID, FOLIO,
                    FECHA, HORA, CAJERO_ID, CLIENTE_ID, DIR_CLI_ID,
                    ALMACEN_ID, MONEDA_ID, IMPUESTO_INCLUIDO, TIPO_CAMBIO,
                    ESTATUS, APLICADO, PROCESO_ORIGEN, SISTEMA_ORIGEN, VENDEDOR_ID,
                    IMPORTE_NETO, TOTAL_IMPUESTOS, TOTAL_RETENCIONES,
                    PESO_EMBARQUE, DESCRIPCION, ES_CFD, ENVIADO,
                    CFDI_CERTIFICADO, CARGAR_SUN, USUARIO_CREADOR,
                    FECHA_HORA_CREACION
                ) VALUES (
                    -1, @CajaId, 'V', @SucursalId, @Folio,
                    @Fecha, @Hora, @CajeroId, @ClienteId, @DirCliId,
                    @AlmacenId, @MonedaId, 'N', 1.0,
                    'N', 'N', 'N', 'PV', @VendedorId,
                    0.0, 0.0, 0.0,
                    0.0, @Descripcion, 'N', 'N',
                    'N', 'N', @UsuarioCreador,
                    @FechaHoraCreacion
                ) RETURNING DOCTO_PV_ID;";

            int generatedDoctoPvId = await connection.ExecuteScalarAsync<int>(
                insertHeaderSql,
                new
                {
                    CajaId = cajaId,
                    SucursalId = sucursalId,
                    Folio = folio,
                    Fecha = dto.FechaHora.Date,
                    Hora = new TimeSpan(timeOfDay.Hours, timeOfDay.Minutes, timeOfDay.Seconds),
                    CajeroId = cajeroId,
                    ClienteId = dto.ClienteId,
                    DirCliId = dirCliId.Value,
                    AlmacenId = almacenId,
                    MonedaId = monedaId,
                    VendedorId = dto.VendedorId,
                    Descripcion = desc,
                    UsuarioCreador = usuarioCreador,
                    FechaHoraCreacion = dto.FechaHora
                },
                transaction: transaction);

            await transaction.CommitAsync();

            return (generatedDoctoPvId, folio);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "[ERROR NO VENTA] Error al registrar no venta en DOCTOS_PV: VentaMovilId={VentaMovilId}, VendedorId={VendedorId}, ClienteId={ClienteId}, CausaId={CausaId}, CausaDesc={CausaDesc}",
                dto?.VentaMovilId, dto?.VendedorId, dto?.ClienteId, dto?.CausaId, dto?.CausaDesc);
            throw;
        }
    }
}

// ============================================================================
// INTERFAZ: IVentaServicePv
// ============================================================================
public interface IVentaServicePv
{
    /// <summary>
    /// Registra una venta. La identidad (caja, cajero, almacen, sucursal,
    /// usuario creador) se resuelve del login (claims JWT).
    /// </summary>
    Task<VentaPvResponseDto> RegistrarVentaPvAsync(UsuarioSesion sesion, VentaPvCreateDto ventaDto);
    /// <summary>
    /// Registra una no-venta en Firebird. Devuelve (DoctoPvId, Folio).
    /// La foto ya no se incluye en DOCTOS_PV.DESCRIPCION; la gestiona la saga.
    /// </summary>
    Task<(int DoctoPvId, string Folio)> RegistrarNoVentaPvAsync(UsuarioSesion sesion, NoVentaPvCreateDto dto);
    Task<bool> AplicarVentaAsync(int doctoPvId);
    Task<bool> CancelarVentaAsync(int doctoPvId);
    Task<IEnumerable<dynamic>> ConsultarTicketAsync(int doctoPvId);
}
