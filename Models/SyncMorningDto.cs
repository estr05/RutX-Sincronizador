namespace Rutx.Sincronizador.Models;

/// <summary>
/// DTO de un cliente asignado al vendedor.
/// Las propiedades en PascalCase se serializan automaticamente
/// a snake_case por el JsonNamingPolicy configurado en Program.cs.
/// Alias SQL: "AS ClienteId", "AS NombreCliente", etc.
/// </summary>
public class ClienteDto
{
    public int     ClienteId     { get; set; }
    public string  NombreCliente { get; set; } = string.Empty;
    public decimal LimiteCredito { get; set; }
    public int     CondPagoId    { get; set; }

    // Crédito: saldo pendiente del cliente (SALDOS_CC, suma historica)
    // y si aun puede recibir crédito (misma regla que valida la venta:
    // limite <= 0 = sin restriccion; si no, saldo debe ser < limite).
    public decimal Saldo        { get; set; }
    public bool    PuedeCredito { get; set; }

    // Dirección -- viene del INNER JOIN con DIRS_CLIENTES
    public string Calle         { get; set; } = string.Empty;
    public string Colonia       { get; set; } = string.Empty;
    public string Poblacion     { get; set; } = string.Empty;
    public string CodigoPostal  { get; set; } = string.Empty;
}

/// <summary>
/// DTO de un articulo activo del catalogo de Microsip.
/// Datos desde CHOCOLATES.fdb:
///   - precio: Precio SIN impuestos desde PRECIOS_ARTICULOS (lista 42)
///   - precio_con_impuesto: Precio final = precio * PRODUCTO(1 + pctje/100)
///     (impuestos compuestos, ej: IVA 16% + IEPS 3% -> 1.16 * 1.03)
///   - impuestos: Lista de impuestos reales del articulo
///   - existencias: Inventario en SALDOS_IN del almacen (carga disponible)
/// </summary>
public class ProductoDto
{
    public int    ArticuloId          { get; set; }
    public string Clave               { get; set; } = string.Empty;
    public string Nombre              { get; set; } = string.Empty;
    public string Estatus             { get; set; } = string.Empty;
    public double? Precio             { get; set; }    // Sin impuestos
    public double PrecioConImpuesto   { get; set; }    // Con impuestos compuestos
    public int    PorcentajeImpuesto  { get; set; }   // Impuesto principal (0, 16, etc.)
    public int    ImpuestoId          { get; set; }    // Impuesto principal (622, etc.)
    public double Existencias         { get; set; }    // SALDOS_IN del almacen
    public List<ImpuestoProductoDto> Impuestos { get; set; } = new();
}

/// <summary>
/// Impuesto real de un articulo (desde IMPUESTOS_ARTICULOS + IMPUESTOS).
/// </summary>
public class ImpuestoProductoDto
{
    public int ImpuestoId { get; set; }
    public decimal PctjeImpuesto { get; set; }
}

/// <summary>
/// Envelope principal de la respuesta de sincronizacion matutina.
/// Contiene todos los catalogos que el vendedor necesita para trabajar offline.
/// </summary>
public class SyncMorningResponseDto
{
    public string           Mensaje    { get; set; } = "Sincronizacion matutina exitosa";
    public int              VendedorId { get; set; }
    public List<ClienteDto> Clientes   { get; set; } = [];
    public List<ProductoDto> Productos { get; set; } = [];
}
