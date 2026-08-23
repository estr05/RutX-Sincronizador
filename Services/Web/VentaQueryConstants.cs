using System.Globalization;
using Microsoft.Extensions.Configuration;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Fuente única de verdad para filtros y ventanas temporales de reportes web.
/// Compartida entre DashboardWebService y ReportsWebService para mantener consistencia 
/// en las reglas de negocio (qué se considera una venta, plazos, etc).
/// </summary>
internal static class VentaQueryConstants
{
    // ====================================================================
    // 1. REGLAS DE NEGOCIO: DEFINICIÓN DE VENTAS
    // ====================================================================

    /// <summary>
    /// Documento de venta de contado o crédito válido.
    /// Excluye explícitamente los documentos marcados como "NO VENTA:" en la descripción.
    /// </summary>
    internal const string CondicionVenta =
        "pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' " +
        "AND (pv.DESCRIPCION IS NULL OR pv.DESCRIPCION NOT LIKE 'NO VENTA:%')";

    /// <summary>
    /// Documento registrado formalmente como "no venta" (ej. cliente cerrado).
    /// </summary>
    internal const string CondicionNoVenta =
        "pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' AND pv.DESCRIPCION LIKE 'NO VENTA:%'";

    /// <summary>
    /// Obtiene las formas de cobro configuradas como crédito en appsettings.
    /// Retorna un arreglo con -1 si no hay configuración, para que el IN(..) SQL sea válido pero vacío.
    /// </summary>
    internal static int[] LeerFormasCredito(IConfiguration configuration)
    {
        var formas = configuration
            .GetSection("MicrosipSettings:CreditFormaCobroIds")
            .Get<int[]>();
        
        return formas is { Length: > 0 } ? formas : new[] { -1 };
    }


    // ====================================================================
    // 2. PARSING Y NORMALIZACIÓN
    // ====================================================================

    /// <summary>
    /// Garantiza que el rango siempre sea uno de: "diario", "semanal", "mensual".
    /// Cae a "diario" por defecto ante cualquier valor inválido.
    /// </summary>
    internal static string NormalizarRango(string? rango)
        => rango?.Trim().ToLowerInvariant() is "semanal" or "mensual"
            ? rango.Trim().ToLowerInvariant()
            : "diario";

    /// <summary>
    /// Parsea una fecha en formato exacto "yyyy-MM-dd" (contrato v2).
    /// </summary>
    internal static DateTime? ParsearFecha(string? fecha)
        => DateTime.TryParseExact(fecha, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var valor)
            ? valor : null;


    // ====================================================================
    // 3. RESOLUCIÓN DE VENTANAS TEMPORALES
    // ====================================================================

    /// <summary>
    /// Ventana de fechas para indicadores resumen (cajas KPI).
    /// - Diario: Hoy a Hoy
    /// - Semanal: Lunes de esta semana a Hoy
    /// - Mensual: Día 1 del mes actual a Hoy
    /// </summary>
    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaResumen(ReportFilterQuery filtros)
    {
        var explicita = VentanaExplicita(filtros);
        if (explicita.HasValue) return explicita.Value;

        var hoy = DateTime.Today;
        return NormalizarRango(filtros.Range) switch
        {
            "semanal" => (LunesDe(hoy), hoy),
            "mensual" => (new DateTime(hoy.Year, hoy.Month, 1), hoy),
            _ => (hoy, hoy),
        };
    }

    /// <summary>
    /// Ventana de fechas para gráficas de series (datos históricos).
    /// - Diario: Solo hoy (para ver 24 horas)
    /// - Semanal: Últimos 7 días (HOY-6 a HOY inclusive)
    /// - Mensual: Últimos 12 meses (Día 1 de hace 11 meses a Hoy)
    /// </summary>
    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaSerie(ReportFilterQuery filtros, string rango)
    {
        var explicita = VentanaExplicita(filtros);
        if (explicita.HasValue) return explicita.Value;

        var hoy = DateTime.Today;
        return rango switch
        {
            "mensual" => (new DateTime(hoy.Year, hoy.Month, 1).AddMonths(-11), hoy),
            "semanal" => (LunesDe(hoy), hoy),     // Igual que los KPIs: lunes→hoy
            _ => (hoy, hoy),                       // Diario: solo hoy
        };
    }

    /// <summary>
    /// Extrae las fechas explícitas Desde/Hasta si ambas están presentes y son válidas.
    /// </summary>
    private static (DateTime, DateTime)? VentanaExplicita(ReportFilterQuery filtros)
    {
        var desde = ParsearFecha(filtros.DateFrom);
        var hasta = ParsearFecha(filtros.DateTo);
        return desde.HasValue && hasta.HasValue ? (desde.Value, hasta.Value) : null;
    }

    /// <summary>
    /// Devuelve el lunes correspondiente a la semana de la fecha dada.
    /// Firebird asume Domingo=0, esta lógica compensa eso a Lunes=inicio de semana.
    /// </summary>
    internal static DateTime LunesDe(DateTime fecha)
    {
        var desplazamiento = ((int)fecha.DayOfWeek + 6) % 7;
        return fecha.AddDays(-desplazamiento);
    }
}
