using System.Net;
using System.Text.Json;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

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
        // Errores SQL/Firebird: se clasifican para dar un mensaje accionable en espanol.
        var sqlError = SqlErrorClassifier.Clasificar(exception);
        if (sqlError.Tipo != SqlErrorTipo.Desconocido)
        {
            var statusCode = sqlError.Tipo switch
            {
                SqlErrorTipo.OdsNoSoportada => (int)HttpStatusCode.InternalServerError,
                SqlErrorTipo.ErrorConexion => (int)HttpStatusCode.ServiceUnavailable,
                SqlErrorTipo.BloqueoODeadlock => (int)HttpStatusCode.ServiceUnavailable,
                SqlErrorTipo.ViolacionLlaveForanea => (int)HttpStatusCode.Conflict,
                SqlErrorTipo.ValorDuplicado => (int)HttpStatusCode.Conflict,
                SqlErrorTipo.PermisosInsuficientes => (int)HttpStatusCode.Forbidden,
                SqlErrorTipo.BaseDatosSoloLectura => (int)HttpStatusCode.Forbidden,
                _ => (int)HttpStatusCode.BadRequest
            };

            var response = new
            {
                mensaje = sqlError.MensajeAmigable,
                detalle = sqlError.Detalle,
                categoria = sqlError.Tipo.ToString(),
                reintentable = sqlError.EsReintentable
            };

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsync(JsonSerializer.Serialize(response));
            return;
        }

        // Resto de excepciones: mapeo de negocio existente.
        var statusCode2 = exception switch
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

        var response2 = new
        {
            mensaje = statusCode2 switch
            {
                400 => "Solicitud inválida.",
                404 => "Recurso no encontrado.",
                401 => "No autorizado.",
                409 => "Conflicto: la operación ya está en proceso.",
                503 => "El servidor está momentáneamente ocupado. Intenta de nuevo.",
                504 => "El servidor no respondió a tiempo.",
                _ => "Error interno del servidor."
            },
            detalle = exception.Message,
            categoria = "desconocido",
            reintentable = false
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode2;

        await context.Response.WriteAsync(JsonSerializer.Serialize(response2));
    }
}
