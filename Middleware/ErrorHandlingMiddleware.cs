using System.Net;
using System.Text.Json;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Middleware;

public class ErrorHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción no manejada: {Message}", ex.Message);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            ArgumentException => (int)HttpStatusCode.BadRequest,
            FolioSinSeriesException => (int)HttpStatusCode.BadRequest,
            FolioAutoCreacionDeshabilitadaException => (int)HttpStatusCode.BadRequest,
            FolioOcupadoException => (int)HttpStatusCode.Conflict,
            DeadlockTransientException => (int)HttpStatusCode.ServiceUnavailable,
            KeyNotFoundException => (int)HttpStatusCode.NotFound,
            UnauthorizedAccessException => (int)HttpStatusCode.Unauthorized,
            TimeoutException => (int)HttpStatusCode.GatewayTimeout,
            _ => (int)HttpStatusCode.InternalServerError
        };

        var response = new
        {
            mensaje = statusCode switch
            {
                400 => "Solicitud inválida.",
                404 => "Recurso no encontrado.",
                401 => "No autorizado.",
                409 => "Conflicto: la operación ya está en proceso.",
                503 => "El servidor está momentáneamente ocupado. Intenta de nuevo.",
                504 => "El servidor no respondió a tiempo.",
                _ => "Error interno del servidor."
            },
            detalle = exception.Message
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}
