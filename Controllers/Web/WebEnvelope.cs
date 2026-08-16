namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Envelope de respuestas v2 web (contrato v2 §9.1):
/// éxito: data, [meta], [filters], trace_id · error: code, message, [errors], trace_id.
/// Nunca se exponen stack traces, SQL, rutas internas ni credenciales.
/// </summary>
public static class WebEnvelope
{
    public static object Success(object data, object? meta = null, object? filters = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["data"] = data,
            ["trace_id"] = Guid.NewGuid().ToString("N"),
        };
        if (meta != null) envelope["meta"] = meta;
        if (filters != null) envelope["filters"] = filters;
        return envelope;
    }

    public static object Error(string code, string message, object? errors = null)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["trace_id"] = Guid.NewGuid().ToString("N"),
        };
        if (errors != null) envelope["errors"] = errors;
        return envelope;
    }
}
