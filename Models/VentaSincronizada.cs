namespace Rutx.Sincronizador.Models;

/// <summary>
/// Registro de idempotencia de ventas: evita que un reintento del móvil
/// duplique una venta que ya se registró en DOCTOS_PV.
/// Se guarda en SQLite (VentasSincronizadas) junto a la cola offline.
/// </summary>
public class VentaSincronizada
{
    public string VentaMovilId { get; set; } = "";

    public int? DoctoPvId { get; set; }

    public string? Folio { get; set; }

    /// <summary>PROCESANDO (en curso) o COMPLETADO (ya registrada en Microsip).</summary>
    public string Estado { get; set; } = "PROCESANDO";

    public DateTime FechaCreacion { get; set; }
}
