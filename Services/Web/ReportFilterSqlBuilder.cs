using Dapper;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Constructor centralizado de predicados SQL compartidos para filtros de reportes y tableros.
/// </summary>
public static class ReportFilterSqlBuilder
{
    /// <summary>
    /// Construye las condiciones compartidas: fechas, zona, vendedor y formas de crédito.
    /// Si una zona solicitada está fuera del alcance del usuario, se inyecta "1=0".
    /// </summary>
    public static (List<string> Condiciones, DynamicParameters Valores) Construir(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        (DateTime Desde, DateTime Hasta) ventana,
        bool incluirFormasCredito,
        int[] formasCredito)
    {
        var condiciones = new List<string>
        {
            "pv.FECHA >= @desde",
            "pv.FECHA <= @hasta",
        };

        var valores = new DynamicParameters();
        valores.Add("@desde", ventana.Desde.Date);
        valores.Add("@hasta", ventana.Hasta.Date);

        if (filtros.RouteId is int ruta)
        {
            condiciones.Add("pv.VENDEDOR_ID = @ruta");
            valores.Add("@ruta", ruta);
        }

        List<int>? zonasActivas;
        if (filtros.ZoneId is int zona)
        {
            if (userZoneIds.Count == 0 || userZoneIds.Contains(zona))
            {
                zonasActivas = new List<int> { zona };
            }
            else
            {
                // Zona solicitada fuera de alcance
                condiciones.Add("1=0");
                zonasActivas = null;
            }
        }
        else if (userZoneIds.Count > 0)
        {
            zonasActivas = userZoneIds.ToList();
        }
        else
        {
            zonasActivas = null;
        }

        if (zonasActivas != null)
        {
            condiciones.Add("EXISTS (SELECT 1 FROM CLIENTES cz WHERE cz.CLIENTE_ID = pv.CLIENTE_ID AND cz.ZONA_CLIENTE_ID IN @zonas)");
            valores.Add("@zonas", zonasActivas);
        }

        if (incluirFormasCredito)
        {
            valores.Add("@formasCredito", formasCredito);
        }

        return (condiciones, valores);
    }
}
