namespace Rutx.Sincronizador.Middleware;

/// <summary>
/// Genera y propaga el trace_id de la frontera web (contrato v2 §9.1).
///
/// - Acepta un `X-Trace-Id` entrante (propagación entre componentes).
/// - Si no llega, genera un GUID "N" y lo deja en HttpContext.Items["trace_id"]
///   para que controladores y el ErrorHandlingMiddleware lo reutilicen.
/// - Expone el valor en la respuesta (header X-Trace-Id) para correlación.
///
/// Debe registrarse ANTES que ErrorHandlingMiddleware en el pipeline.
/// </summary>
public sealed class WebTraceIdMiddleware
{
    private readonly RequestDelegate _next;

    public WebTraceIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = context.Request.Headers["X-Trace-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(traceId))
            traceId = Guid.NewGuid().ToString("N");

        context.Items["trace_id"] = traceId;
        context.Response.Headers["X-Trace-Id"] = traceId;

        await _next(context);
    }
}