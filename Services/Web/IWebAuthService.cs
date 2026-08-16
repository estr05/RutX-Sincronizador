using System.Security.Claims;
using Rutx.Sincronizador.Models.Web.Auth;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Autenticación del portal (contrato v2 §5). El login se hace SOLO contra
/// la BD complementaria (web_users): los usuarios del portal son de oficina
/// y NUNCA se autentican contra Firebird/Microsip ni contra stubs.
/// </summary>
public interface IWebAuthService
{
    /// <summary>
    /// Valida credenciales, resuelve roles/permisos/zonas y emite el JWT web.
    /// Fallo → resultado con código funcional (UNAUTHENTICATED /
    /// ACCOUNT_DISABLED) y mensaje del contrato v2 §5.2.
    /// </summary>
    Task<WebAuthResult> LoginAsync(WebLoginRequest request, string? ipAddress, string? traceId, CancellationToken cancellationToken = default);

    /// <summary>Identidad + claims desde el token (GET /auth/me).</summary>
    WebAuthResult Me(ClaimsPrincipal principal);

    /// <summary>Audita el cierre de sesión del portal (POST /auth/logout → 204).</summary>
    Task LogoutAuditAsync(long? userId, string? username, string? ipAddress, string? traceId, CancellationToken cancellationToken = default);
}

public sealed record WebAuthResult(
    bool IsSuccess,
    string? Code,
    string? Message,
    WebLoginResponse? Response = null)
{
    public static WebAuthResult Exito(WebLoginResponse response) => new(true, null, null, response);

    public static WebAuthResult Error(string code, string message) => new(false, code, message, null);
}