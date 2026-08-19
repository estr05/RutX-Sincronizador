using System.Net;
using System.Text.Json;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Models.Web;
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
        var (statusCode, mensaje, detalle, categoria, reintentable) = Clasificar(exception);

        // Frontera web v2 (contrato §9.1): envelope { code, message, errors, trace_id }.
        // Aislada del formato legado; móvil (/api/v1) y administrador local
        // (/api/v2/admin) conservan { mensaje, detalle, categoria, reintentable }.
        if (context.Request.Path.StartsWithSegments("/api/v2/web"))
        {
            var web = new
            {
                code = CodigoWeb(statusCode),
                message = mensaje,
                errors = (object?)null,
                trace_id = context.Items["trace_id"] as string ?? Guid.NewGuid().ToString("N"),
            };

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsync(JsonSerializer.Serialize(web));
            return;
        }

        var legado = new
        {
            mensaje,
            detalle,
            categoria,
            reintentable,
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsync(JsonSerializer.Serialize(legado));
    }

    /// <summary>Clasificación única de excepciones (comportamiento existente, sin cambios).</summary>
    private static (int Status, string Mensaje, string Detalle, string Categoria, bool Reintentable) Clasificar(Exception exception)
    {
        // Errores SQL/Firebird: se clasifican para dar un mensaje accionable en español.
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
            return (statusCode, sqlError.MensajeAmigable, sqlError.Detalle, sqlError.Tipo.ToString(), sqlError.EsReintentable);
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
            FeatureNotReadyException => (int)HttpStatusCode.NotImplemented,
            _ => (int)HttpStatusCode.InternalServerError
        };

        return (statusCode2,
            statusCode2 switch
            {
                400 => "Solicitud inválida.",
                404 => "Recurso no encontrado.",
                401 => "No autorizado.",
                409 => "Conflicto: la operación ya está en proceso.",
                501 => exception.Message, // Mostrar el mensaje exacto de FeatureNotReadyException
                503 => "El servidor está momentáneamente ocupado. Intenta de nuevo.",
                504 => "El servidor no respondió a tiempo.",
                _ => "Error interno del servidor."
            },
            exception.Message,
            "desconocido",
            false);
    }

    private static string CodigoWeb(int statusCode) => statusCode switch
    {
        400 => "BAD_REQUEST",
        401 => "UNAUTHORIZED",
        403 => "FORBIDDEN",
        404 => "NOT_FOUND",
        409 => "CONFLICT",
        422 => "VALIDATION_ERROR",
        501 => "FEATURE_NOT_READY",
        503 => "SERVICE_UNAVAILABLE",
        504 => "GATEWAY_TIMEOUT",
        _ => "INTERNAL_ERROR",
    };
}