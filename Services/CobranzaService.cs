// ============================================================================
// ARCHIVO: CobranzaService.cs
// PROPOSITO: Servicio para registrar cobranzas (pagos/abonos) de ventas a
//            credito en Microsip ERP sobre Firebird CHOCOLATES.FDB.
//
// DEFINICION:
//   Cobranza = cuando un cliente paga total o parcialmente una venta
//   que hizo a credito. Se registra como DOCTOS_PV con TIPO_DOCTO='P'.
//
// FLUJO CONTRA MICROSIP:
//   =========================================================================
//   PASO 1: INSERT DOCTOS_PV (cabecera)
//     - TIPO_DOCTO = 'P' (Pago/Cobro)
//     - ESTATUS = 'N' (Nuevo)
//     - APLICADO = 'N' (aun no aplicado)
//     - El trigger DOCTOS_PV_BEFINS genera DOCTO_PV_ID usando ID_DOCTOS
//
//   PASO 2: INSERT DOCTOS_PV_COBROS (forma de pago)
//     - FORMA_COBRO_ID = 67 (EFECTIVO), 2845 (TARJETA), etc.
//     - TIPO = 'C' (Complete, pago completo)
//     - El trigger registra en MOVTOS_EFVO_CAJA si es efectivo
//
//   PASO 3: INSERT DOCTOS_PV_LIGAS (vinculo con la venta original)
//     - DOCTO_PV_FTE_ID = El nuevo documento 'P' (quien paga)
//     - DOCTO_PV_DEST_ID = El documento 'V' original (la venta a credito)
//     - Esto permite rastrear que pago corresponde a que venta
//
//   PASO 4: INSERT DOCTOS_CC (abono en CxC)
//     - NATURALEZA_CONCEPTO = 'R' (Abono/Credito)
//     - CONCEPTO_CC_ID = 11 (Cobro) o 155 (Cobro en mostrador)
//     - IMPORTE_COBRO = monto pagado
//     - APLICADO = 'S' (aplicado directamente)
//     - El trigger DOCTOS_CC_BEFINS genera DOCTO_CC_ID
//
//   PASO 5: INSERT DOCTOS_ENTRE_SIS (vinculo PV -> CC)
//     - CLAVE_SIS_FTE = 'PV', DOCTO_FTE_ID = nuevo DoctoPvId
//     - CLAVE_SIS_DEST = 'CC', DOCTO_DEST_ID = nuevo DoctoCcId
//     - TIPO_DOCTO = 'P' (indica que es un pago/cobro)
//
//   PASO 6: UPDATE APLICADO='S' en DOCTOS_PV
//     - Dispara trigger DOCTOS_PV_AFTUPD_0
//     - El trigger ejecuta APLICA_DOCTO_PV
//     - Para TIPO_DOCTO='P', llama APLICA_COBRO_CC_PV
//     - APLICA_COBRO_CC_PV busca en DOCTOS_ENTRE_SIS y hace:
//       UPDATE DOCTOS_CC SET APLICADO='S' (ya esta hecho, redundante)
//
//   =========================================================================
//   IMPORTANTE sobre DOCTOS_CC_LIGAS:
//     La tabla DOCTOS_CC_LIGAS NO existe en esta base de Microsip.
//     El vinculo entre el abono y el cargo original se maneja a traves
//     de DOCTOS_ENTRE_SIS y la logica del trigger.
//   =========================================================================
//
// TABLAS MODIFICADAS:
//   DOCTOS_PV           -> Cabecera del documento de pago
//   DOCTOS_PV_COBROS    -> Forma(s) de cobro del pago
//   DOCTOS_PV_LIGAS     -> Vinculo con la venta original
//   DOCTOS_CC           -> Abono en Cuentas por Cobrar
//   DOCTOS_ENTRE_SIS    -> Vinculo PV->CC entre subsistemas
// ============================================================================

using System;
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
/// Servicio para registrar cobranzas (pago de ventas a credito).
/// Cada cobranza crea un DOCTOS_PV (TIPO_DOCTO='P') y su correspondiente
/// abono en DOCTOS_CC para reflejar la reduccion del saldo pendiente.
/// </summary>
public class CobranzaService : ICobranzaService
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly IFkResolverService _fkResolver;
    private readonly IFolioService _folioService;
    private readonly IFolioLockService _folioLock;
    private readonly IFirebirdRetryPolicy _firebirdRetry;
    private readonly ILogger<CobranzaService> _logger;

    public CobranzaService(
        IConfiguration configuration,
        IFkResolverService fkResolver,
        ILogger<CobranzaService> logger,
        IFolioService? folioService = null,
        IFolioLockService? folioLock = null,
        IFirebirdRetryPolicy? firebirdRetry = null)
    {
        _configuration = configuration;
        _fkResolver = fkResolver;
        _logger = logger;
        _folioService = folioService ?? new FolioService(configuration, NullLogger<FolioService>.Instance);
        _folioLock = folioLock ?? new FolioLockService(configuration, NullLogger<FolioLockService>.Instance);
        _firebirdRetry = firebirdRetry ?? new FirebirdRetryPolicy(configuration, NullLogger<FirebirdRetryPolicy>.Instance);
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
    }

    /// <summary>
    /// Registra una cobranza completa en una sola transaccion.
    /// Crea DOCTOS_PV (TIPO_DOCTO='P') + DOCTOS_PV_COBROS +
    /// DOCTOS_PV_LIGAS (vinculo a venta original) + DOCTOS_CC (abono) +
    /// DOCTOS_ENTRE_SIS (vinculo PV->CC).
    /// </summary>
    public async Task<CobranzaResponseDto> InsertCobranzaAsync(CobranzaCreateDto dto)
    {
        if (dto == null)
            throw new ArgumentNullException(nameof(dto));

        if (dto.Pagos == null || !dto.Pagos.Any())
            throw new ArgumentException("La cobranza debe contener al menos una forma de pago.", nameof(dto));

        if (dto.DocumentosCobrar == null || !dto.DocumentosCobrar.Any())
            throw new ArgumentException("La cobranza debe especificar al menos un documento a pagar.", nameof(dto));

        // La caja se resuelve antes del candado para saber qué candado tomar.
        var (cajaId, almacenId) = await ResolverCajaYAlmacenAsync(dto);

        await using var _ = await _folioLock.AcquireAsync(cajaId);
        return await _firebirdRetry.EjecutarAsync(() => EjecutarCobranzaCoreAsync(dto, cajaId, almacenId));
    }

    private async Task<(int CajaId, int AlmacenId)> ResolverCajaYAlmacenAsync(CobranzaCreateDto dto)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        int defaultAlmacenId = _configuration.GetValue<int?>("MicrosipSettings:DefaultAlmacenId") ?? 19;
        int cajaId;
        int almacenId = defaultAlmacenId;

        if (dto.CajaId.HasValue)
        {
            cajaId = dto.CajaId.Value;
            var cajaAlmacen = await connection.QueryFirstOrDefaultAsync<int?>(
                "SELECT ALMACEN_ID FROM CAJAS WHERE CAJA_ID = @CajaId",
                new { CajaId = cajaId });
            if (cajaAlmacen.HasValue)
                almacenId = cajaAlmacen.Value;
        }
        else
        {
            var cajaDefault = await connection.QueryFirstOrDefaultAsync(@"
                SELECT FIRST 1 CAJA_ID, ALMACEN_ID FROM CAJAS
                WHERE OCULTO = 'N' ORDER BY CAJA_ID");
            if (cajaDefault == null)
                throw new InvalidOperationException("No se encontraron cajas activas.");
            cajaId = Convert.ToInt32(cajaDefault.CAJA_ID);
            almacenId = Convert.ToInt32(cajaDefault.ALMACEN_ID);
        }

        return (cajaId, almacenId);
    }

    private async Task<CobranzaResponseDto> EjecutarCobranzaCoreAsync(CobranzaCreateDto dto, int cajaId, int almacenId)
    {
        // Valores por defecto desde configuracion
        int sucursalId = _configuration.GetValue<int?>("MicrosipSettings:DefaultSucursalId") ?? 4274;
        int defaultMonedaId = _configuration.GetValue<int>("MicrosipSettings:DefaultMonedaId", 1);

        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // ================================================================
            // PASO 1: Reserva atomica de folio para TIPO_DOCTO='P'
            // (UPDATE ... RETURNING, sin SELECT-then-UPDATE).
            // ================================================================
            string folio = await _folioService.ReservarFolioAsync(connection, transaction, cajaId, "P");

            // Obtener o resolver cajero
            int cajeroId;
            if (dto.CajeroId.HasValue)
            {
                cajeroId = dto.CajeroId.Value;
            }
            else
            {
                cajeroId = await _fkResolver.ResolveFkAsync(
                    connection, transaction,
                    "CAJEROS_A_DOCTOS_PV", null, null,
                    _configuration.GetValue<int>("MicrosipSettings:DefaultCajeroId", 2419));
            }

            // Calcular total cobrado
            decimal totalCobrado = dto.Pagos.Sum(p => p.Importe);

            var timeOfDay = dto.FechaHora.TimeOfDay;

            // ================================================================
            // PASO 2: INSERT cabecera DOCTOS_PV con TIPO_DOCTO='P'
            // ================================================================
            // TIPO_DOCTO='P' indica que es un Pago/Cobro.
            // El trigger DOCTOS_PV_BEFINS genera DOCTO_PV_ID con ID_DOCTOS.
            string insertHeaderSql = @"
                INSERT INTO DOCTOS_PV (
                    DOCTO_PV_ID, CAJA_ID, TIPO_DOCTO, SUCURSAL_ID, FOLIO,
                    FECHA, HORA, CAJERO_ID, CLIENTE_ID,
                    ALMACEN_ID, MONEDA_ID, IMPUESTO_INCLUIDO, TIPO_CAMBIO,
                    ESTATUS, APLICADO, PROCESO_ORIGEN, SISTEMA_ORIGEN, VENDEDOR_ID,
                    IMPORTE_NETO, TOTAL_IMPUESTOS, TOTAL_RETENCIONES,
                    PESO_EMBARQUE, DESCRIPCION, ES_CFD, ENVIADO,
                    CFDI_CERTIFICADO, CARGAR_SUN
                ) VALUES (
                    -1, @CajaId, 'P', @SucursalId, @Folio,
                    @Fecha, @Hora, @CajeroId, @ClienteId,
                    @AlmacenId, @MonedaId, 'N', 1.0,
                    'N', 'N', 'N', 'PV', @VendedorId,
                    0.0, 0.0, 0.0,
                    0.0, 'COBRANZA: Pago de venta a credito', 'N', 'N',
                    'N', 'N'
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
                    AlmacenId = almacenId,
                    MonedaId = defaultMonedaId,
                    VendedorId = dto.VendedorId
                },
                transaction: transaction);

            // ================================================================
            // PASO 3: INSERT formas de cobro DOCTOS_PV_COBROS
            // ================================================================
            // TIPO='C' (Complete) porque este es un pago real,
            // no una porcion a credito.
            // El trigger DOCTOS_PV_COBROS_AFTINS_0 registra automaticamente
            // en MOVTOS_EFVO_CAJA si la forma de cobro lo requiere.
            string insertPaymentSql = @"
                INSERT INTO DOCTOS_PV_COBROS (
                    DOCTO_PV_COBRO_ID, DOCTO_PV_ID, TIPO, FORMA_COBRO_ID,
                    IMPORTE, TIPO_CAMBIO, IMPORTE_MON_DOC
                ) VALUES (
                    -1, @DoctoPvId, 'C', @FormaCobroId,
                    @Importe, 1.0, @Importe
                );";

            foreach (var pago in dto.Pagos)
            {
                await connection.ExecuteAsync(insertPaymentSql, new
                {
                    DoctoPvId = generatedDoctoPvId,
                    FormaCobroId = pago.FormaCobroId,
                    Importe = pago.Importe
                }, transaction: transaction);
            }

            // ================================================================
            // PASO 4: INSERT DOCTOS_PV_LIGAS (vinculo a venta(s) original)
            // ================================================================
            // Vincula el nuevo documento 'P' (quien paga) con el documento
            // 'V' original (la venta a credito).
            //
            // DOCTO_PV_FTE_ID = El documento que genera la liga (el pago 'P')
            // DOCTO_PV_DEST_ID = El documento destino (la venta 'V' original)
            //
            // El trigger DOCTOS_PV_LIGAS_BEFINS genera el ID automaticamente.
            string insertLigaSql = @"
                INSERT INTO DOCTOS_PV_LIGAS (
                    DOCTO_PV_LIGA_ID, DOCTO_PV_FTE_ID, DOCTO_PV_DEST_ID
                ) VALUES (
                    -1, @DoctoPvFteId, @DoctoPvDestId
                );";

            foreach (var doc in dto.DocumentosCobrar)
            {
                await connection.ExecuteAsync(insertLigaSql, new
                {
                    DoctoPvFteId = generatedDoctoPvId,
                    DoctoPvDestId = doc.DoctoPvOriginalId
                }, transaction: transaction);
            }

            // ================================================================
            // PASO 5: INSERT DOCTOS_CC (ABONO en CxC)
            // ================================================================
            // Creamos manualmente un DOCTOS_CC con NATURALEZA='R' (Abono)
            // para reflejar la reduccion del saldo pendiente del cliente.
            //
            // El trigger DOCTOS_CC_BEFINS genera DOCTO_CC_ID con ID_DOCTOS.
            //
            // CONCEPTO_CC_ID:
            //   11 = Cobro (TIPO='P', NATURALEZA='R')
            //   155 = Cobro en mostrador (TIPO='P', NATURALEZA='R')
            //
            // IMPORTANTE: Usamos CONCEPTO_CC_ID=11 (Cobro generico)
            // porque el concepto "en mostrador" es para ventas de mostrador.
            //
            // DOCTOS_CC NO tiene columna TIPO_DOCTO. La naturaleza del
            // documento se define por NATURALEZA_CONCEPTO:
            //   'C' = Cargo (venta a credito, aumenta saldo)
            //   'R' = Abono (pago/cobro, disminuye saldo)
            string insertCcSql = @"
                INSERT INTO DOCTOS_CC (
                    DOCTO_CC_ID, CONCEPTO_CC_ID, NATURALEZA_CONCEPTO,
                    FOLIO, SUCURSAL_ID, FECHA, HORA,
                    CLIENTE_ID, IMPORTE_COBRO, TIPO_CAMBIO,
                    CANCELADO, APLICADO, SISTEMA_ORIGEN,
                    ESTATUS, ESTATUS_ANT, ES_CFD, TIENE_ANTICIPO,
                    ENVIADO, CFDI_CERTIFICADO, MODALIDAD_FACTURACION,
                    CONTABILIZADO_GYP, INTEG_BA, CONTABILIZADO_BA
                ) VALUES (
                    -1, @ConceptoCcId, 'R',
                    @Folio, @SucursalId, @Fecha, @Hora,
                    @ClienteId, @ImporteCobro, 1.0,
                    'N', 'S', 'PV',
                    'N', 'N', 'N', 'N',
                    'N', 'N', 'PREIMP',
                    'N', 'N', 'N'
                ) RETURNING DOCTO_CC_ID;";

            int conceptoAbonoId = _configuration.GetValue<int>("MicrosipSettings:DefaultConceptoCobroId", 11);

            int generatedDoctoCcId = await connection.ExecuteScalarAsync<int>(
                insertCcSql,
                new
                {
                    ConceptoCcId = conceptoAbonoId,
                    Folio = folio,
                    SucursalId = sucursalId,
                    Fecha = dto.FechaHora.Date,
                    Hora = new TimeSpan(timeOfDay.Hours, timeOfDay.Minutes, timeOfDay.Seconds),
                    ClienteId = dto.ClienteId,
                    ImporteCobro = totalCobrado
                },
                transaction: transaction);

            // ================================================================
            // PASO 6: INSERT DOCTOS_ENTRE_SIS (vinculo PV->CC)
            // ================================================================
            // Esta tabla vincula documentos entre subsistemas de Microsip.
            // 'PV' -> Punto de Venta, 'CC' -> Cuentas por Cobrar.
            //
            // El trigger APLICA_COBRO_CC_PV busca aqui para encontrar
            // el DOCTOS_CC correspondiente y marcarlo como APLICADO='S'.
            //
            // TIPO_DOCTO en DOCTOS_ENTRE_SIS:
            //   'C' = Cargo (creado por GENERA_DOCTO_CC_PV en ventas)
            //   'P' = Pago (creado manualmente en cobranzas)
            //   'D' = Devolucion
            string insertEntreSisSql = @"
                INSERT INTO DOCTOS_ENTRE_SIS (
                    CLAVE_SIS_FTE, DOCTO_FTE_ID,
                    CLAVE_SIS_DEST, DOCTO_DEST_ID, TIPO_DOCTO
                ) VALUES (
                    'PV', @DoctoFteId,
                    'CC', @DoctoDestId, 'P'
                );";

            await connection.ExecuteAsync(insertEntreSisSql, new
            {
                DoctoFteId = generatedDoctoPvId,
                DoctoDestId = generatedDoctoCcId
            }, transaction: transaction);

            // ================================================================
            // PASO 7: APLICAR el documento (dispara trigger)
            // ================================================================
            // UPDATE APLICADO='S' dispara DOCTOS_PV_AFTUPD_0
            // -> APLICA_DOCTO_PV -> APLICA_COBRO_CC_PV
            //
            // APLICA_COBRO_CC_PV busca en DOCTOS_ENTRE_SIS el registro
            // que acabamos de crear (PV->CC) y ejecuta:
            //   UPDATE DOCTOS_CC SET APLICADO='S'
            // Esto ya esta hecho (paso 5), asi que es redundante pero seguro.
            await connection.ExecuteAsync(@"
                UPDATE DOCTOS_PV SET APLICADO = 'S', FECHA_VIGENCIA = CURRENT_DATE
                WHERE DOCTO_PV_ID = @DoctoPvId AND APLICADO = 'N'",
                new { DoctoPvId = generatedDoctoPvId },
                transaction: transaction);

            // Confirmar transaccion
            await transaction.CommitAsync();

            return new CobranzaResponseDto
            {
                Message = "Cobranza registrada exitosamente",
                CobranzaMovilId = dto.CobranzaMovilId,
                DoctoPvId = generatedDoctoPvId,
                Folio = folio,
                TotalCobrado = totalCobrado,
                DocumentosPagados = dto.DocumentosCobrar.Count
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "[ERROR COBRANZA] Fallo al registrar cobranza: CobranzaMovilId={CobranzaMovilId}, VendedorId={VendedorId}, ClienteId={ClienteId}, Pagos={Pagos}, Docs={Docs}",
                dto.CobranzaMovilId, dto.VendedorId, dto.ClienteId, dto.Pagos?.Count ?? 0, dto.DocumentosCobrar?.Count ?? 0);
            throw;
        }
    }
}

// ============================================================================
// INTERFAZ: ICobranzaService
// ============================================================================
public interface ICobranzaService
{
    /// <summary>
    /// Registra una cobranza (pago de venta a credito).
    /// Crea DOCTOS_PV (TIPO_DOCTO='P') + abono en DOCTOS_CC.
    /// </summary>
    Task<CobranzaResponseDto> InsertCobranzaAsync(CobranzaCreateDto dto);
}
