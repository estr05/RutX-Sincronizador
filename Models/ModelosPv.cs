// ============================================================================
// ARCHIVO: ModelosPv.cs
// PROPOSITO: DTOs para el modulo Punto de Venta (PV) de Microsip
//            + Ventas en Ruta + Credito + Cobranza
// BASE DE DATOS: CHOCOLATES.FDB (Firebird 3.0)
//
// TABLAS MICROSIP INVOLUCRADAS:
//   DOCTOS_PV           -> Cabecera del ticket (71 columnas)
//   DOCTOS_PV_DET       -> Detalle/renglones del ticket
//   DOCTOS_PV_COBROS    -> Formas de cobro/pago por ticket
//   DOCTOS_PV_LIGAS     -> Relacion entre documentos PV (venta <-> cobranza)
//   DOCTOS_CC           -> Cuentas por Cobrar (cargos y abonos)
//   DOCTOS_ENTRE_SIS    -> Vinculo PV <-> CC entre sistemas
//   DOCTOS_CC_LIGAS     -> Relacion entre cargo y abono en CxC
//   SALDOS_CC           -> Saldos de CxC por cliente/mes
//   VENCIMIENTOS_CARGOS_CC -> Calendario de vencimientos parciales
//   IMPUESTOS_DOCTOS_PV -> Resumen de IVA por documento
//   FOLIOS_CAJAS        -> Control de folios por caja
//   CLIENTES            -> Maestro de clientes
//   ARTICULOS           -> Maestro de articulos
//   FORMAS_COBRO        -> Catalogo de formas de cobro
//   VENDEDORES          -> Vendedores (rutas)
//   AGENTES             -> Usuarios del sistema (login)
//   CAJAS               -> Puntos de venta (cajas)
//   IMPUESTOS           -> Catalogo de impuestos
//   CONDICIONES_PAGO    -> Condiciones de pago (credito)
//   CONCEPTOS_CC        -> Conceptos de CxC (Venta, Cobro, etc.)
//
// FLUJO CREDITO (automatico via trigger):
//   1. INSERT DOCTOS_PV con TIPO_DOCTO='V' y APLICADO='N'
//   2. INSERT DOCTOS_PV_COBROS con FORMA_COBRO_ID=71 (CREDITO)
//   3. UPDATE APLICADO='S' -> DISPARA trigger DOCTOS_PV_AFTUPD_0
//   4. Trigger ejecuta APLICA_DOCTO_PV -> APLICA_VTA_PV
//   5. APLICA_VTA_PV verifica INTEG_CC_PV='S' -> llama GENERA_DOCTO_CC_PV
//   6. GENERA_DOCTO_CC_PV crea:
//      - DOCTOS_CC con IMPORTE_COBRO = total, NATURALEZA='C' (Cargo)
//      - DOCTOS_ENTRE_SIS vinculando PV->CC
//      - SALDOS_CC actualizado (CARGOS_CXC += total)
//      - VENCIMIENTOS_CARGOS_CC segun CONDICIONES_PAGO del cliente
//
// FLUJO COBRANZA (manual, trigger solo marca APLICADO):
//   1. INSERT DOCTOS_PV con TIPO_DOCTO='P' (Pago)
//   2. INSERT DOCTOS_PV_COBROS con forma de pago real (EFECTIVO, etc.)
//   3. INSERT manual DOCTOS_CC con NATURALEZA='R' (Abono)
//   4. INSERT manual DOCTOS_ENTRE_SIS vinculando PV->CC
//   5. INSERT manual DOCTOS_CC_LIGAS vinculando abono -> cargo original
//   6. UPDATE APLICADO='S' -> trigger APLICA_COBRO_CC_PV
//      (solo marca APLICADO='S' en DOCTOS_CC, redundante pero inofensivo)
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Rutx.Sincronizador.Models;

// ============================================================================
// SECCION 1: DTOs de VENTA PV
// ============================================================================

/// <summary>
/// DTO principal para registrar una venta en DOCTOS_PV (Punto de Venta).
/// 
/// En VENTAS EN RUTA:
///   - VendedorId = ID del vendedor (FK -> VENDEDORES)
///   - CajeroId/CajaId/AlmacenId/SucursalId = los inyecta el servidor
///     desde los claims del token (identidad resuelta en el login)
///   - UsuarioCreador = lo inyecta el servidor (USUARIO_CREADOR del ticket)
/// </summary>
public class VentaPvCreateDto
{
    /// <summary>ID unico de la venta en el dispositivo movil (para idempotencia)</summary>
    [Required]
    public string VentaMovilId { get; set; } = string.Empty;

    /// <summary>ID del vendedor que realiza la venta (FK -> VENDEDORES)</summary>
    [Required]
    public int VendedorId { get; set; }

    /// <summary>ID del cliente (FK -> CLIENTES)</summary>
    [Required]
    public int ClienteId { get; set; }

    /// <summary>Fecha y hora de la venta</summary>
    [Required]
    public DateTime FechaHora { get; set; }

    /// <summary>ID de la caja (FK -> CAJAS). Lo inyecta el servidor desde los claims del token.</summary>
    public int? CajaId { get; set; }

    /// <summary>ID del cajero. Lo inyecta el servidor desde los claims del token.</summary>
    public int? CajeroId { get; set; }

    /// <summary>ID del almacen (FK -> ALMACENES). Lo inyecta el servidor desde los claims del token.</summary>
    public int? AlmacenId { get; set; }

    /// <summary>ID de la sucursal (FK -> SUCURSALES). Lo inyecta el servidor desde los claims del token.</summary>
    public int? SucursalId { get; set; }

    /// <summary>Usuario que crea el documento (USUARIO_CREADOR). Lo inyecta el servidor desde el token.</summary>
    public string? UsuarioCreador { get; set; }

    /// <summary>ID de la forma de cobro (FK -> FORMAS_COBRO): 67=Efectivo, 71=Credito, 2845=Tarjeta</summary>
    [Required]
    public int FormaCobroId { get; set; }

    /// <summary>Notas u observaciones (max 200 caracteres)</summary>
    public string? Notas { get; set; }

    /// <summary>
    /// Lista de formas de cobro para pagos mixtos.
    /// Si tiene datos, se IGNORA FormaCobroId.
    /// Si esta vacio, se usa FormaCobroId (compatibilidad).
    ///
    /// EJEMPLO: Venta mixta $800 efectivo + $200 credito
    ///   Pagos = [
    ///     { FormaCobroId: 67, Importe: 800.00 },
    ///     { FormaCobroId: 71, Importe: 200.00 }
    ///   ]
    ///
    /// FORMAS_COBRO disponibles:
    ///   67  = EFECTIVO (TIPO='E')
    ///   71  = CREDITO (TIPO='R')
    ///   2845 = TARJETA DEBITO (TIPO='T')
    ///   3702 = Transferencia (TIPO='O')
    /// </summary>
    public List<CobroDto>? Pagos { get; set; }

    /// <summary>Lista de articulos vendidos</summary>
    [Required]
    public List<DetalleVentaPvDto> Detalles { get; set; } = new();
}

/// <summary>
/// DTO para registrar una "No Venta" en DOCTOS_PV.
/// Se guardara con TIPO_DOCTO = 'N' y el detalle de la causa en DESCRIPCION.
/// </summary>
public class NoVentaPvCreateDto
{
    [Required]
    public string VentaMovilId { get; set; } = string.Empty;

    [Required]
    public int VendedorId { get; set; }

    [Required]
    public int ClienteId { get; set; }

    [Required]
    public DateTime FechaHora { get; set; }

    public int? CajaId { get; set; }
    public int? CajeroId { get; set; }

    /// <summary>Usuario que crea el documento. Lo inyecta el servidor desde el token.</summary>
    public string? UsuarioCreador { get; set; }

    [Required]
    public int CausaId { get; set; }
    
    [Required]
    public string CausaDesc { get; set; } = string.Empty;
    
    public string? Comentario { get; set; }
    public string? FotoPath { get; set; }
}

/// <summary>
/// Cada renglon de la venta. Mapea a DOCTOS_PV_DET.
/// El trigger DOCTOS_PV_DET_BEFINS genera automaticamente:
///   - DOCTO_PV_DET_ID si se pasa -1
///   - POSICION si se pasa -1 (calcula la siguiente disponible)
/// </summary>
public class DetalleVentaPvDto
{
    /// <summary>ID del articulo (FK -> ARTICULOS)</summary>
    [Required]
    public int ArticuloId { get; set; }

    /// <summary>Cantidad vendida (puede tener decimales, ej: 1.5 kg)</summary>
    [Required]
    public decimal Unidades { get; set; }

    /// <summary>Precio unitario SIN impuesto (columna PRECIO_UNITARIO)</summary>
    [Required]
    public decimal PrecioUnitario { get; set; }

    /// <summary>ID del impuesto (FK -> IMPUESTOS): 622=IVA 16%, 3344=IVA 0%, 2204=IEPS 3%</summary>
    [Required]
    public int ImpuestoId { get; set; }

    /// <summary>
    /// Lista COMPLETA de impuestos del articulo (impuestos compuestos).
    /// Si viene con datos, se aplican TODOS (factor = ∏(1 + pctje/100), ej:
    /// IVA 16% + IEPS 3% -> 1.16 * 1.03 = 1.1948) y [ImpuestoId] se ignora.
    /// Si es null o vacia, se usa [ImpuestoId] (compatibilidad).
    /// </summary>
    public List<ImpuestoDetallePvDto>? Impuestos { get; set; }
}

/// <summary>
/// Impuesto individual de un detalle (impuestos compuestos).
/// </summary>
public class ImpuestoDetallePvDto
{
    /// <summary>ID del impuesto (FK -> IMPUESTOS). El servidor recarga el porcentaje real por ID.</summary>
    [Required]
    public int ImpuestoId { get; set; }

    /// <summary>
    /// Porcentaje del impuesto enviado por el movil (ej: 16 = 16%).
    /// Se usa SOLO como fallback si el servidor no encuentra el impuesto en el catalogo.
    /// </summary>
    public decimal PctjeImpuesto { get; set; }
}

/// <summary>
/// Respuesta de la API al registrar una venta exitosamente
/// </summary>
public class VentaPvResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string VentaMovilId { get; set; } = string.Empty;
    public int DoctoPvId { get; set; }
    public string Folio { get; set; } = string.Empty;
    public int TotalRenglones { get; set; }
    public decimal ImporteNeto { get; set; }
    public decimal TotalImpuestos { get; set; }
    public decimal TotalDocumento { get; set; }

    /// <summary>Indica si la respuesta es una reproduccion de una venta ya registrada (idempotencia).</summary>
    public bool Reutilizada { get; set; }
}

// ============================================================================
// SECCION 2: DTOs de CONSULTA / AUXILIARES
// ============================================================================

/// <summary>
/// Datos del cliente para la venta (desde CLIENTES + DIRS_CLIENTES)
/// </summary>
public class ClienteVentaInfo
{
    public int ClienteId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public int MonedaId { get; set; }
    public int CondPagoId { get; set; }
    public int? DirCliId { get; set; }
    public decimal LimiteCredito { get; set; }
    public decimal Saldo { get; set; }
}

/// <summary>
/// Datos de la caja asignada (desde CAJAS)
/// </summary>
public class CajaInfo
{
    public int CajaId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public int AlmacenId { get; set; }
    public int? FormaCobroPredetId { get; set; }
    public bool ManejaVendedores { get; set; }
}

// ============================================================================
// SECCION 3: DTOs de VENTAS EN RUTA
// ============================================================================

// ============================================================================
// SECCION 3A: DTOs FISCALES (Datos del emisor y establecimiento)
// ============================================================================

/// <summary>
/// Datos fiscales del emisor (la empresa).
/// Se obtienen desde RFCS_LCO (primer RFC activo).
/// </summary>
public class EmisorFiscalDto
{
    /// <summary>RFC de la empresa (ej: XXXX123456XXX)</summary>
    public string Rfc { get; set; } = string.Empty;

    /// <summary>Razon social o nombre fiscal completo</summary>
    public string NombreFiscal { get; set; } = string.Empty;

    /// <summary>Domicilio fiscal registrado en SAT</summary>
    public string DomicilioFiscal { get; set; } = string.Empty;

    /// <summary>Regimen fiscal (codigo del SAT, ej: 601=General de Ley)</summary>
    public string RegimenFiscal { get; set; } = string.Empty;
}

/// <summary>
/// Datos del establecimiento (sucursal) donde se realiza la venta.
/// Se obtienen desde SUCURSALES usando el SUCURSAL_ID del vendedor.
/// Estos datos se muestran en el ticket del comprobante.
/// </summary>
public class SucursalDto
{
    /// <summary>ID de la sucursal en Microsip</summary>
    public int SucursalId { get; set; }

    /// <summary>Nombre del establecimiento (ej: "SUCURSAL CENTRO")</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Calle o avenida (ej: "Av. Reforma")</summary>
    public string Calle { get; set; } = string.Empty;

    /// <summary>Numero exterior</summary>
    public string NumExterior { get; set; } = string.Empty;

    /// <summary>Numero interior (departamento, oficina, etc.)</summary>
    public string NumInterior { get; set; } = string.Empty;

    /// <summary>Colonia o fraccionamiento</summary>
    public string Colonia { get; set; } = string.Empty;

    /// <summary>Poblacion o ciudad</summary>
    public string Poblacion { get; set; } = string.Empty;

    /// <summary>Codigo postal</summary>
    public string CodigoPostal { get; set; } = string.Empty;

    /// <summary>Telefono del establecimiento</summary>
    public string Telefono { get; set; } = string.Empty;
}

// ============================================================================
// SECCION 3B: DTOs de VENTAS EN RUTA (continuacion)
// ============================================================================

/// <summary>
/// Informacion del vendedor para la sincronizacion matutina.
/// Relacion AGENTES -> VENDEDORES -> CLIENTES
/// </summary>
public class VendedorRutaInfo
{
    public int VendedorId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public int AgenteId { get; set; }
    public string Agente { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public int SucursalId { get; set; }
    public int AlmacenId { get; set; }
}

/// <summary>
/// Response de la sincronizacion matutina. Contiene todo lo que necesita el vendedor
/// para empezar la ruta: datos del vendedor, caja, ruta asignada, clientes de la ruta,
/// productos y formas de cobro.
/// 
/// FLUJO DE DATOS:
///   VENDEDOR (695) -> AGENTE (4165) -> RUTA (1451703) -> RUTAS_DET -> CLIENTES
/// </summary>
public class SyncMorningRutaResponseDto
{
    public string Mensaje { get; set; } = "Sincronizacion matutina exitosa";
    public VendedorRutaInfo Vendedor { get; set; } = null!;
    public CajaInfo Caja { get; set; } = null!;
    public RutaInfo? Ruta { get; set; }
    public List<ClienteDto> Clientes { get; set; } = new();
    public List<ProductoDto> Productos { get; set; } = new();
    public List<FormaCobroDto> FormasCobro { get; set; } = new();

    /// <summary>Datos fiscales del emisor (RFC, razon social, regimen)</summary>
    public EmisorFiscalDto? Emisor { get; set; }

    /// <summary>Datos del establecimiento (direccion, telefono)</summary>
    public SucursalDto? Sucursal { get; set; }
}

/// <summary>
/// Informacion de la ruta asignada al vendedor.
/// Mapea a la tabla RUTAS con sus datos principales.
/// Los clientes de la ruta estan en RUTAS_DET, vinculados por RUTA_ID.
/// </summary>
public class RutaInfo
{
    /// <summary>ID de la ruta (PK en RUTAS)</summary>
    public int RutaId { get; set; }
    
    /// <summary>Nombre descriptivo de la ruta (ej: "RUTA ZONA NORTE")</summary>
    public string Nombre { get; set; } = string.Empty;
    
    /// <summary>Clave corta de la ruta (ej: "RZN01")</summary>
    public string Clave { get; set; } = string.Empty;
    
    /// <summary>Fecha de la ruta (hoy). Se calcula al sincronizar.</summary>
    public string Fecha { get; set; } = string.Empty;
    
    /// <summary>Dia de la semana en espanol correspondiente a la fecha actual</summary>
    public string DiaSemana { get; set; } = string.Empty;
    
    /// <summary>Numero del dia en RUTAS_DET (1=Lunes...7=Domingo)</summary>
    public int DiaNumero { get; set; }
    
    /// <summary>Total de clientes asignados a esta ruta para el dia de hoy</summary>
    public int TotalClientesHoy { get; set; }
    
    /// <summary>Total de clientes asignados a esta ruta en total</summary>
    public int TotalClientesRuta { get; set; }
}

/// <summary>
/// Forma de cobro (desde FORMAS_COBRO)
/// DATOS REALES: 67=EFECTIVO, 68=CHEQUE, 71=CREDITO, 703=CREDITO 15D,
///               2205=CREDITO 30D, 2845=TARJETA DEBITO, 3702=TRANSFERENCIA
/// </summary>
public class FormaCobroDto
{
    public int FormaCobroId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty; // E=Efectivo, C=Cheque, R=Credito, O=Otro
}

// NOTA: ClienteDto y ProductoDto ya estan definidos en Models/SyncMorningDto.cs
// Se reutilizan para la sincronizacion matutina de rutas.

/// <summary>
/// Request para el cierre de ruta al final del dia.
/// </summary>
public class RouteCloseRequestDto
{
    // Obsoleto: la identidad (usuario/caja/cajero) se resuelve del token JWT.
    // Se conserva solo por compatibilidad con clientes antiguos.
    public int VendedorId { get; set; }

    [Required]
    public DateTime Fecha { get; set; }

    public string? Ruta { get; set; }

    /// <summary>IDs de las ventas realizadas (DOCTO_PV_ID)</summary>
    public List<int> VentasRealizadas { get; set; } = new();

    public decimal TotalEfectivo { get; set; }
    public decimal TotalTarjeta { get; set; }
    public decimal TotalCredito { get; set; }

    /// <summary>Inventario final de articulos (sobrantes/faltantes)</summary>
    public List<InventarioFinalDto> InventarioFinal { get; set; } = new();

    public decimal CombustibleGastado { get; set; }
    public decimal KilometrosRecorridos { get; set; }
    public string? Observaciones { get; set; }
}

/// <summary>
/// Articulo en el inventario final de la ruta
/// </summary>
public class InventarioFinalDto
{
    public int ArticuloId { get; set; }
    public decimal UnidadesRestantes { get; set; }
}

/// <summary>
/// Resumen diario de ventas para mostrar en el cierre
/// </summary>
public class CierreJornadaDto
{
    public string Ruta { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }

    // Visitas y Efectividad
    public int ClientesVisitados { get; set; }
    public int Efectividad { get; set; }
    public int NoVentas { get; set; }
    public int DevolucionesCount { get; set; }

    // Preventas
    public int TotalProductosSolicitados { get; set; }
    public int Preventas { get; set; }
    public decimal TotalPreventa { get; set; }

    // Entregas
    public decimal EntregasContado { get; set; }
    public decimal EntregasCredito { get; set; }

    // Ventas
    public decimal Contado { get; set; }
    public decimal Credito { get; set; }

    // Cobranza
    public decimal Cobrado { get; set; }
    public decimal CobradoEfectivo { get; set; }

    // Devoluciones
    public decimal Devolucion { get; set; }
    public decimal DevolucionContado { get; set; }
    public decimal DevolucionCredito { get; set; }

    // Gastos
    public decimal GastosOperativos { get; set; }

    // Cierre
    public decimal TotalCierre { get; set; }
    
    // Detalle extra si se requiere
    public List<ResumenFormaCobroDto> DetalleCobros { get; set; } = new();
}

/// <summary>
/// Total por forma de cobro
/// </summary>
public class ResumenFormaCobroDto
{
    public string FormaCobro { get; set; } = string.Empty;
    public int TotalTickets { get; set; }
    public decimal TotalCobrado { get; set; }
}

/// <summary>
/// Respuesta del cierre de ruta con validacion de diferencias.
/// Diferencia = TotalReportado - TotalCobradoReal
///   Positivo (+) = SOBRANTE
///   Negativo (-) = FALTANTE
/// </summary>
public class RouteCloseResponseDto
{
    public string Mensaje { get; set; } = string.Empty;

    public int VendedorId { get; set; }
    public DateTime Fecha { get; set; }

    public int TotalVentasValidas { get; set; }
    public int TotalVentasInvalidas { get; set; }
    public List<int> VentasInvalidas { get; set; } = new();

    public CierreJornadaDto ResumenReal { get; set; } = new();

    public decimal TotalEfectivoReportado { get; set; }
    public decimal TotalTarjetaReportado { get; set; }
    public decimal TotalCreditoReportado { get; set; }
    public decimal TotalReportado { get; set; }

    public decimal Diferencia { get; set; }
    public bool HayDiferencia { get; set; }

    public decimal CombustibleGastado { get; set; }
    public decimal KilometrosRecorridos { get; set; }
    public string? Observaciones { get; set; }
}

// ============================================================================
// SECCION 4: DTOs de COBRANZA (Pago de creditos)
// ============================================================================

/// <summary>
/// DTO para registrar una cobranza (pago/abono a una venta a credito).
///
/// Una cobranza representa cuando el cliente paga total o parcialmente
/// una venta que hizo a credito. Se registra como un DOCTOS_PV con
/// TIPO_DOCTO='P' y se vincula al documento original via DOCTOS_PV_LIGAS.
///
/// DIFERENCIA CON VentaPvCreateDto:
///   VentaPvCreateDto  -> TIPO_DOCTO='V' (venta nueva, con o sin credito)
///   CobranzaCreateDto -> TIPO_DOCTO='P' (pago de una venta previa a credito)
///
/// FLUJO COMPLETO:
///   1. Se crea DOCTOS_PV (TIPO_DOCTO='P') que representa el pago
///   2. Se registra DOCTOS_PV_COBROS con la forma de pago (efectivo, etc.)
///   3. Se vincula al documento de venta original via DOCTOS_PV_LIGAS
///      con TIPO_LIGA='C' (Cobranza)
///   4. Se crea manualmente DOCTOS_CC como ABONO (NATURALEZA='R')
///      para reflejar la reduccion del saldo pendiente
///   5. Se vincula en DOCTOS_ENTRE_SIS y DOCTOS_CC_LIGAS
///   6. Se aplica el documento (APLICADO='S')
/// </summary>
public class CobranzaCreateDto
{
    /// <summary>ID unico en el dispositivo movil (para idempotencia)</summary>
    [Required]
    public string CobranzaMovilId { get; set; } = string.Empty;

    /// <summary>ID del vendedor que cobra (FK -> VENDEDORES)</summary>
    [Required]
    public int VendedorId { get; set; }

    /// <summary>ID del cliente que paga (FK -> CLIENTES)</summary>
    [Required]
    public int ClienteId { get; set; }

    /// <summary>Fecha y hora del cobro</summary>
    [Required]
    public DateTime FechaHora { get; set; }

    /// <summary>ID de la caja (FK -> CAJAS). null = buscar primera activa</summary>
    public int? CajaId { get; set; }

    /// <summary>ID del cajero. null = se asigna automaticamente</summary>
    public int? CajeroId { get; set; }

    /// <summary>Lista de pagos individuales (cada uno con su forma de cobro)</summary>
    [Required]
    public List<CobroDto> Pagos { get; set; } = new();

    /// <summary>Documentos de venta original que se estan pagando</summary>
    [Required]
    public List<DocumentoCobranzaDto> DocumentosCobrar { get; set; } = new();
}

/// <summary>
/// Representa un pago individual dentro de una cobranza.
/// Una cobranza puede tener MULTIPLES formas de cobro:
///   Ej: $500 en EFECTIVO + $300 en TARJETA = $800 total
///
/// FORMAS_COBRO disponibles:
///   67  = EFECTIVO (TIPO='E')
///   71  = CREDITO (TIPO='R')  -- OJO: no usar credito para pagar credito
///   2845 = TARJETA DEBITO (TIPO='T')
///   3702 = Transferencia (TIPO='O')
/// </summary>
public class CobroDto
{
    /// <summary>ID de la forma de cobro (FK -> FORMAS_COBRO)</summary>
    [Required]
    public int FormaCobroId { get; set; }

    /// <summary>Monto pagado con esta forma de cobro</summary>
    [Required]
    public decimal Importe { get; set; }
}

/// <summary>
/// Documento de venta original que se esta pagando.
/// Una cobranza puede pagar MULTIPLES documentos a la vez
/// (el cliente paga varias cuentas pendientes).
///
/// TIPO_LIGA en DOCTOS_PV_LIGAS:
///   'C' = Cobranza (pago aplicado a un documento)
///   'D' = Devolucion
///   'S' = Sustitucion
/// </summary>
public class DocumentoCobranzaDto
{
    /// <summary>ID del DOCTOS_PV original (la venta a credito)</summary>
    [Required]
    public int DoctoPvOriginalId { get; set; }

    /// <summary>Monto que se paga de este documento (puede ser parcial)</summary>
    [Required]
    public decimal ImportePagado { get; set; }
}

/// <summary>
/// Respuesta al registrar una cobranza exitosamente
/// </summary>
public class CobranzaResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string CobranzaMovilId { get; set; } = string.Empty;
    public int DoctoPvId { get; set; }
    public string Folio { get; set; } = string.Empty;
    public decimal TotalCobrado { get; set; }
    public int DocumentosPagados { get; set; }
}

// ============================================================================
// SECCION 5: DTOs de CREDITO (Consulta de saldos pendientes)
// ============================================================================

/// <summary>
/// DTO para la respuesta de pedidos/credito.
/// Muestra el saldo pendiente por cliente basado en SALDOS_CC.
///
/// SALDOS_CC almacena los saldos de CxC por cliente, mes y ano:
///   CARGOS_CXC   = Total de cargos (ventas a credito) en el mes
///   CREDITOS_CXC = Total de abonos (pagos recibidos) en el mes
///   Saldo pendiente = SUM(CARGOS_CXC) - SUM(CREDITOS_CXC)
///
/// Si el cliente no tiene saldo en SALDOS_CC, se asume 0.
/// Si LIMITE_CREDITO = 0, significa que no tiene limite o es contado.
/// </summary>
public class CreditoClienteDto
{
    /// <summary>ID del cliente (FK -> CLIENTES)</summary>
    public int ClienteId { get; set; }

    /// <summary>Nombre del cliente</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Limite de credito asignado (desde CLIENTES.LIMITE_CREDITO)</summary>
    public decimal LimiteCredito { get; set; }

    /// <summary>Saldo actual pendiente de pago</summary>
    public decimal SaldoPendiente { get; set; }

    /// <summary>Porcentaje del credito usado (0-100)</summary>
    public decimal PorcentajeUsado { get; set; }

    /// <summary>Total de documentos pendientes (cantidad de ventas sin pagar)</summary>
    public int DocumentosPendientes { get; set; }

    /// <summary>Dias de atraso en el pago (0 si esta al dia)</summary>
    public int DiasAtraso { get; set; }
}

/// <summary>
/// Respuesta con la lista de creditos pendientes
/// </summary>
public class PedidosCreditoResponseDto
{
    public string Message { get; set; } = string.Empty;
    public int TotalClientes { get; set; }
    public List<CreditoClienteDto> Creditos { get; set; } = new();
}

// ============================================================================
// SECCION 6: DTOs de DOCUMENTOS PENDIENTES (para pantalla de cobranza)
// ============================================================================

/// <summary>
/// DTO para un documento de venta original pendiente de pago.
/// Se usa en GET /api/v1/credito/clientes/{id}/documentos para listar
/// las facturas/ventas 'V' que el cliente aun no ha pagado.
///
/// RELACION CON TRIGGERS:
///   GENERA_DOCTO_CC_PV (llamado por APLICA_VTA_PV via trigger DOCTOS_PV_AFTUPD_0)
///   crea automaticamente DOCTOS_CC (cargo) + DOCTOS_ENTRE_SIS con TIPO_DOCTO='C'.
///   El saldo pendiente se calcula restando los abonos ('R') aplicados manualmente
///   por CobranzaService.
/// </summary>
public class DocumentoPendienteDto
{
    /// <summary>ID del DOCTOS_PV original (venta a credito)</summary>
    public int DoctoPvOriginalId { get; set; }

    /// <summary>Folio del ticket de venta (ej: V00000123)</summary>
    public string Folio { get; set; } = string.Empty;

    /// <summary>Fecha de la venta original</summary>
    public DateTime Fecha { get; set; }

    /// <summary>Monto original de la venta (IMPORTE_NETO de DOCTOS_PV)</summary>
    public decimal ImporteOriginal { get; set; }

    /// <summary>Total del cargo generado en DOCTOS_CC (puede diferir del neto)</summary>
    public decimal TotalCargo { get; set; }

    /// <summary>Total ya abonado a este cargo (suma de DOCTOS_CC con NATURALEZA='R')</summary>
    public decimal TotalAbonado { get; set; }

    /// <summary>Saldo pendiente = TotalCargo - TotalAbonado</summary>
    public decimal SaldoPendiente { get; set; }
}

/// <summary>
/// Respuesta del endpoint GET /api/v1/credito/clientes/{id}/documentos
/// </summary>
public class DocumentosPendientesResponseDto
{
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public List<DocumentoPendienteDto> Documentos { get; set; } = new();
}

// ============================================================================
// SECCION 7: Propiedad Pagos en VentaPvCreateDto (pagos mixtos)
// ============================================================================

/// <summary>
/// Propiedad Pagos para soportar multiples formas de cobro en una venta.
/// Se agrega a VentaPvCreateDto.
/// Si se proporciona Pagos, se ignora la propiedad individual FormaCobroId.
///
/// BACKWARD COMPATIBLE:
///   - Si Pagos tiene datos -> se usan los pagos
///   - Si Pagos esta vacio -> se usa FormaCobroId (comportamiento anterior)
/// </summary>
// La propiedad Pagos se agrega directamente en la clase VentaPvCreateDto
