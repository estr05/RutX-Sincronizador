using System.Security.Claims;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Lectura de claims del portal (scope=web) desde el principal autenticado.
/// Los valores inválidos se descartan: un claim roto jamás concede alcance.
/// </summary>
public static class WebClaims
{
    public static IReadOnlyList<int> Zonas(ClaimsPrincipal? principal)
    {
        var zonas = principal?.FindAll("zone_ids")
            .Select(c => int.TryParse(c.Value, out var z) ? z : -1)
            .Where(z => z >= 0)
            .ToList();

        if (zonas == null || zonas.Count == 0)
        {
            bool isAdmin = principal?.FindAll("roles").Any(c => c.Value == Rutx.Sincronizador.Services.Web.WebRoleCatalog.Administrador) == true ||
                           principal?.IsInRole(Rutx.Sincronizador.Services.Web.WebRoleCatalog.Administrador) == true;
            if (!isAdmin)
            {
                // Un usuario sin zonas y sin alcance global no debe ver nada.
                return new[] { -1 };
            }
        }

        return zonas?.ToArray() ?? Array.Empty<int>();
    }

    public static long? UserId(ClaimsPrincipal principal)
        => long.TryParse(principal.FindFirst("user_id")?.Value, out var id) ? id : null;

    public static string? Username(ClaimsPrincipal principal)
        => principal.FindFirst("username")?.Value;
}