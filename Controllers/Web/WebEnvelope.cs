namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Envelope de respuestas v2 web (contrato v2 §9.1):
/// éxito: data, [meta], [filters], trace_id · error: code, message, [errors], trace_id.
/// Nunca se exponen stack traces, SQL, rutas internas ni credenciales.
///
/// El trace_id se toma de HttpContext.Items["trace_id"] (WebTraceIdMiddleware)
/// para garantizar correlación real con el header X-Trace-Id de la respuesta.
/// </summary>
public static class WebEnvelope
{
    private static string ResolveTraceId(HttpContext? httpContext)
        => httpContext?.Items["trace_id"]?.ToString() ?? Guid.NewGuid().ToString("N");

    public static object Success(HttpContext httpContext, object data, object? meta = null, object? filters = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["data"] = data,
            ["trace_id"] = ResolveTraceId(httpContext),
        };
        if (meta != null) envelope["meta"] = meta;
        if (filters != null) envelope["filters"] = filters;
        return envelope;
    }

    public static object Error(HttpContext httpContext, string code, string message, object? errors = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["trace_id"] = ResolveTraceId(httpContext),
        };
        if (errors != null) envelope["errors"] = errors;
        return envelope;
    }
}
