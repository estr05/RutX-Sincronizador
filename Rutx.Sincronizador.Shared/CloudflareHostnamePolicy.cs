using System.Net;
using System.Text.RegularExpressions;

namespace Rutx.Sincronizador.Shared;

/// <summary>
/// Politica central del hostname publico usado para el acceso remoto via
/// Cloudflare Tunnel. Unica fuente de verdad para que el wizard, el helper y
/// el validador de produccion concuerden respecto a que es un hostname real
/// y valido.
///
/// Reglas:
///   - Siempre se sanea primero (se quita protocolo, puerto, ruta, '/' final).
///   - Se exige un FQDN DNS real: no vacio, no placeholder, no IP, no
///     localhost, sin espacios ni caracteres invalidos.
///   - Se rechaza el placeholder por defecto "sync.ejemplo.com".
/// </summary>
public static class CloudflareHostnamePolicy
{
    private const string PlaceholderMarca = "CHANGE_ME";
    private const string PlaceholderEjemplo = "sync.ejemplo.com";

    private static readonly Regex EtiquetaValida = new(
        @"^(?=.{1,253}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Limpia un hostname en bruto (protocolo, puerto, ruta, barras) y
    /// devuelve solo el dominio. Si el resultado queda vacio, devuelve "".
    /// </summary>
    public static string Sanitizar(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var h = raw.Trim();

        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            h = h["https://".Length..];
        else if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            h = h["http://".Length..];

        var slashIdx = h.IndexOf('/');
        if (slashIdx >= 0) h = h[..slashIdx];

        var colonIdx = h.IndexOf(':');
        if (colonIdx >= 0) h = h[..colonIdx];

        h = h.TrimEnd('/').Trim();

        return h;
    }

    /// <summary>
    /// Valida que el hostname saneado sea un FQDN real utilizable como
    /// hostname publico de Cloudflare. Devuelve true si es valido y setea el
    /// mensaje de error cuando no lo es.
    /// </summary>
    public static bool EsFqdnValido(string sanitized, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            error = "Debes indicar un Hostname publico al habilitar el acceso remoto.";
            return false;
        }

        if (sanitized.Equals(PlaceholderEjemplo, StringComparison.OrdinalIgnoreCase))
        {
            error = "El hostname no puede ser el placeholder por defecto 'sync.ejemplo.com'.";
            return false;
        }

        if (sanitized.Contains(PlaceholderMarca, StringComparison.OrdinalIgnoreCase) ||
            sanitized.Contains('<') || sanitized.Contains('>') || sanitized.Contains(' '))
        {
            error = "El hostname contiene un placeholder o caracteres no validos.";
            return false;
        }

        if (IPAddress.TryParse(sanitized, out _) ||
            sanitized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            sanitized.Equals("0.0.0.0"))
        {
            error = "El hostname debe ser un nombre de dominio publico real (no una IP ni localhost).";
            return false;
        }

        if (Uri.CheckHostName(sanitized) != UriHostNameType.Dns ||
            !EtiquetaValida.IsMatch(sanitized) ||
            !sanitized.Contains('.'))
        {
            error = $"El hostname '{sanitized}' no es un FQDN DNS valido.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Conveniencia: sanea y valida en un solo paso. Devuelve el hostname
    /// saneado si es valido, o null y el error cuando no lo es.
    /// </summary>
    public static string? SanitizarYValidar(string? raw, out string error)
    {
        var limpio = Sanitizar(raw);
        if (EsFqdnValido(limpio, out error))
            return limpio;
        return null;
    }
}
