namespace Rutx.Sincronizador.Models;

/// <summary>
/// Clasificacion de una operacion de inventario leida desde Microsip.
///   - Transfer: par S (origen) + E (destino) en el mismo DOCTO (traspaso).
///   - Seed:    entrada "Saldo inicial" sin pareja (siembra de stock no rastreado).
///   - Anomalia: entrada/salida sin su pareja en el mismo DOCTO, o que viola
///               la regla (traspaso sin existencia en origen, saldo inicial
///               sobre producto rastreado).
/// </summary>
public enum TipoOperacionInventario
{
    Transfer,
    Seed,
    Anomalia
}

/// <summary>
/// Linea cruda de movimiento (DOCTOS_IN_DET + DOCTOS_IN) tal como la lee
/// el lector de la BD. Entrada del clasificador.
/// </summary>
public class MovimientoInventarioDto
{
    public long DoctoInId { get; set; }
    public DateTime Fecha { get; set; }
    public int ArticuloId { get; set; }
    public string? ArticuloNombre { get; set; }
    public int ConceptoInId { get; set; }
    public string? ConceptoNombre { get; set; }
    /// <summary>Naturaleza de la linea: 'E' (entrada) o 'S' (salida).</summary>
    public string TipoMovto { get; set; } = "";
    /// <summary>Almacen al que aplica la linea (origen en salidas, destino en entradas).</summary>
    public int AlmacenId { get; set; }
    /// <summary>Almacen destino del encabezado (solo traspasos).</summary>
    public int? AlmacenDestinoId { get; set; }
    public decimal Unidades { get; set; }
    public string Aplicado { get; set; } = "S";
    public string Cancelado { get; set; } = "N";
}

/// <summary>
/// Operacion ya clasificada (Transfer / Seed / Anomalia) lista para reportar.
/// </summary>
public class OperacionInventarioDto
{
    public long DoctoInId { get; set; }
    public DateTime Fecha { get; set; }
    public TipoOperacionInventario Tipo { get; set; }
    public int ArticuloId { get; set; }
    public string? ArticuloNombre { get; set; }
    public decimal Unidades { get; set; }
    public int AlmacenOrigenId { get; set; }
    public int? AlmacenDestinoId { get; set; }
    public int ConceptoInId { get; set; }
    public string? ConceptoNombre { get; set; }
    /// <summary>Motivo, solo para anomalias (por que se marco).</summary>
    public string? Motivo { get; set; }
}

/// <summary>
/// Resultado de una corrida de reconciliacion.
/// </summary>
public class ResultadoReconciliacionDto
{
    public DateTime GeneradoEn { get; set; }
    public DateTime Desde { get; set; }
    public int Transferencias { get; set; }
    public int Semillas { get; set; }
    public int Anomalias { get; set; }
    public List<OperacionInventarioDto> Operaciones { get; set; } = new();
    public List<OperacionInventarioDto> AnomaliasList { get; set; } = new();
}
