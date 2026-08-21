using System.Security.Claims;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Lectura de claims del portal (scope=web) desde el principal autenticado.
/// Los valores inválidos se descartan: un claim roto jamás concede alcance.
/// </summary>
public static class WebClaims
{
    public static IReadOnlyList<int> Zonas(ClaimsPrincipal? principal)
        => principal?.FindAll("zone_ids")
            .Select(c => int.TryParse(c.Value, out var z) ? z : -1)
            .Where(z => z >= 0)
            .ToArray() ?? Array.Empty<int>();

    public static long? UserId(ClaimsPrincipal principal)
        => long.TryParse(principal.FindFirst("user_id")?.Value, out var id) ? id : null;

    public static string? Username(ClaimsPrincipal principal)
        => principal.FindFirst("username")?.Value;
}