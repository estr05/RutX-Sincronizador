using Dapper;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

public class ReportFilterSqlBuilderTests
{
    [Fact]
    public void Construir_FechasYFormasCredito_SiempreSeIncluyen()
    {
        var filtros = new ReportFilterQuery { DateFrom = "2026-08-01", DateTo = "2026-08-31" };
        var ventana = (new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));
        var formasCredito = new[] { 1, 2, 3 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(filtros, Array.Empty<int>(), ventana, true, formasCredito);

        Assert.Contains("pv.FECHA >= @desde", condiciones);
        Assert.Contains("pv.FECHA <= @hasta", condiciones);

        Assert.Equal(ventana.Item1, valores.Get<DateTime>("desde"));
        Assert.Equal(ventana.Item2, valores.Get<DateTime>("hasta"));
        Assert.Equal(formasCredito, valores.Get<int[]>("formasCredito"));
    }

    [Fact]
    public void Construir_ZonaFueraDeAlcance_InyectaFalso()
    {
        var filtros = new ReportFilterQuery { ZoneId = 5 };
        var userZonas = new[] { 1, 2 }; // No tiene la zona 5

        var (condiciones, _) = ReportFilterSqlBuilder.Construir(filtros, userZonas, (DateTime.Today, DateTime.Today), false, Array.Empty<int>());

        Assert.Contains("1=0", condiciones);
    }

    [Fact]
    public void Construir_ListaVacia_DeniegaGlobal()
    {
        // En WebClaims.Zonas inyectamos [-1] para simular lista vac�a sin rol admin
        var userZonas = new[] { -1 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(new ReportFilterQuery(), userZonas, (DateTime.Today, DateTime.Today), false, Array.Empty<int>());

        Assert.Contains("EXISTS (SELECT 1 FROM CLIENTES cz WHERE cz.CLIENTE_ID = pv.CLIENTE_ID AND cz.ZONA_CLIENTE_ID IN @zonas)", condiciones);
        Assert.Equal(userZonas.ToList(), valores.Get<List<int>>("zonas"));
    }

    [Fact]
    public void Construir_Ruta_SeFiltraCorrectamente()
    {
        var filtros = new ReportFilterQuery { RouteId = 99 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(filtros, Array.Empty<int>(), (DateTime.Today, DateTime.Today), false, Array.Empty<int>());

        Assert.Contains("pv.VENDEDOR_ID = @ruta", condiciones);
        Assert.Equal(99, valores.Get<int>("ruta"));
    }

    [Fact]
    public void Construir_RutaConZonasAsignadas_AplicaAmbosPredicados()
    {
        // route_id se resuelve contra VENDEDOR_ID pero SIEMPRE acotado por las zonas del usuario.
        var filtros = new ReportFilterQuery { RouteId = 118 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(
            filtros, new[] { 3792 }, (DateTime.Today, DateTime.Today), true, new[] { 71 });

        Assert.Contains(condiciones, c => c.Contains("pv.VENDEDOR_ID = @ruta"));
        Assert.Contains(condiciones, c => c.Contains("cz.ZONA_CLIENTE_ID IN @zonas"));
        Assert.Equal(118, valores.Get<int>("ruta"));
        Assert.Equal(new[] { 3792 }.ToList(), valores.Get<List<int>>("zonas"));
    }

    [Fact]
    public void Construir_ZonaDentroDelAlcance_SeLimitaAElla()
    {
        var filtros = new ReportFilterQuery { ZoneId = 3793 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(
            filtros, new[] { 3792, 3793 }, (DateTime.Today, DateTime.Today), false, Array.Empty<int>());

        Assert.Contains(condiciones, c => c.Contains("cz.ZONA_CLIENTE_ID IN @zonas"));
        Assert.Equal(new[] { 3793 }.ToList(), valores.Get<List<int>>("zonas"));
    }

    [Fact]
    public void Construir_SinFormasCredito_NoAgregaElParametro()
    {
        var (_, valores) = ReportFilterSqlBuilder.Construir(
            new ReportFilterQuery(), Array.Empty<int>(), (DateTime.Today, DateTime.Today), false, new[] { 71 });

        Assert.DoesNotContain("@formasCredito", valores.ParameterNames);
        Assert.DoesNotContain("formasCredito", valores.ParameterNames);
    }

    [Fact]
    public void Construir_FechasExplicitas_UsanLaVentanaRecibida()
    {
        // El builder no interpreta fechas: usa la ventana ya resuelta por VentaQueryConstants.
        var ventana = (new DateTime(2026, 8, 1), new DateTime(2026, 8, 21));

        var (_, valores) = ReportFilterSqlBuilder.Construir(
            new ReportFilterQuery { DateFrom = "2026-08-01", DateTo = "2026-08-21" },
            Array.Empty<int>(), ventana, false, Array.Empty<int>());

        Assert.Equal(new DateTime(2026, 8, 1), valores.Get<DateTime>("desde"));
        Assert.Equal(new DateTime(2026, 8, 21), valores.Get<DateTime>("hasta"));
    }

    [Fact]
    public void Construir_FechasConHora_SeTruncanParaCompatibilidadConDoctosPvFecha()
    {
        // EVIDENCIA DE ESQUEMA:
        // En DOCTOS_PV la FECHA y la HORA son columnas separadas.
        // FECHA es un DATE estricto (00:00:00).
        // Por tanto, la condicion inclusiva "pv.FECHA <= @hasta" es 100% precisa
        // y abarca todo el dia sin necesidad de un tope exclusivo (< dia+1).
        var ventana = (new DateTime(2026, 8, 1, 15, 30, 0), new DateTime(2026, 8, 21, 23, 59, 59));

        var (_, valores) = ReportFilterSqlBuilder.Construir(
            new ReportFilterQuery(), Array.Empty<int>(), ventana, false, Array.Empty<int>());

        Assert.Equal(new DateTime(2026, 8, 1), valores.Get<DateTime>("desde"));
        Assert.Equal(new DateTime(2026, 8, 21), valores.Get<DateTime>("hasta"));
    }

    [Fact]
    public void Construir_ZonaFueraDeAlcanceConFormasCredito_InyectaFalsoYConservaParametros()
    {
        // Regresión: cca34ee
        var filtros = new ReportFilterQuery { ZoneId = 3 }; // Fuera de alcance
        var userZonas = new[] { 1, 2 }; // Zona diferente
        var formasCredito = new[] { 71, 703, 2205 };

        var (condiciones, valores) = ReportFilterSqlBuilder.Construir(
            filtros, userZonas, (DateTime.Today, DateTime.Today), incluirFormasCredito: true, formasCredito);

        // Debe devolver la denegación...
        Assert.Contains("1=0", condiciones);

        // ... Y ADEMÁS no debe abortar antes de incluir formasCredito (evitando el HTTP 500 / SQL Token Unknown).
        Assert.Contains("formasCredito", valores.ParameterNames);
        Assert.Equal(formasCredito, valores.Get<int[]>("formasCredito"));
    }
}
