namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/dashboard (contrato v2 §6.1).
/// Las propiedades PascalCase se serializan a snake_case por la política
/// JsonNamingPolicy.SnakeCaseLower configurada en Program.cs.
/// </summary>
public class DashboardSummaryResponse
{
    /// <summary>Los 7 KPIs del tablero (venta total, contado, crédito, cobranza, no ventas, entrega, gastos).</summary>
    public List<DashboardKpiDto> Kpi { get; set; } = new();

    public DashboardMetaDto Meta { get; set; } = new();
}

/// <summary>
/// KPI individual del tablero: label, valor numérico, delta opcional y status.
/// </summary>
public class DashboardKpiDto
{
    public string Label { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? Delta { get; set; }
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// Metadatos del tablero: última sincronización y moneda del reporte.
/// </summary>
public class DashboardMetaDto
{
    public DateTime? LastSyncAt { get; set; }
    public string Currency { get; set; } = "MXN";
}
