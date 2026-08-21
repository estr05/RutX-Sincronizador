using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Guardián del contrato de filtros (contrato v2 §8): el portal envía query
/// string en snake_case y ASP.NET NO mapea date_from → DateFrom con
/// [FromQuery] plano. Cada propiedad debe anclarse explícitamente con
/// [FromQuery(Name=...)] o el filtro se descarta en silencio (bug de
/// producción detectado en QA del dashboard).
/// </summary>
public class ReportFilterQueryBindingTests
{
    [Theory]
    [InlineData(nameof(ReportFilterQuery.Range), "range")]
    [InlineData(nameof(ReportFilterQuery.DateFrom), "date_from")]
    [InlineData(nameof(ReportFilterQuery.DateTo), "date_to")]
    [InlineData(nameof(ReportFilterQuery.ZoneId), "zone_id")]
    [InlineData(nameof(ReportFilterQuery.RouteId), "route_id")]
    public void CadaPropiedad_AnclaSuNombreSnakeCase(string propiedad, string nombreEsperado)
    {
        var atributo = typeof(ReportFilterQuery)
            .GetProperty(propiedad)?
            .GetCustomAttribute<FromQueryAttribute>();

        Assert.NotNull(atributo);
        Assert.Equal(nombreEsperado, atributo.Name);
    }

    [Fact]
    public void TodaPropiedadNueva_DebeTraerAnclajeExplicito()
    {
        var sinAnclar = typeof(ReportFilterQuery).GetProperties()
            .Where(p => p.GetCustomAttribute<FromQueryAttribute>() is null)
            .Select(p => p.Name)
            .ToArray();

        Assert.True(
            sinAnclar.Length == 0,
            "Propiedades sin [FromQuery(Name=...)]: " + string.Join(", ", sinAnclar) +
            ". Sin el ancla explícita, el binding descarta el parámetro snake_case en silencio.");
    }
}
