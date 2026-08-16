namespace Rutx.Sincronizador.Models.Web.Auth;

/// <summary>
/// Request de POST /api/v2/web/auth/login (contrato v2 §5).
/// Público con rate limit 5 intentos/IP/minuto; nunca usa stubs.
/// </summary>
public sealed record WebLoginRequest(
    string Username,
    string Password);

/// <summary>
/// Respuesta de login y me: identidad + claims efectivos para la sesión cifrada del portal.
/// </summary>
public sealed record WebUserSessionDto(
    long Id,
    string Username,
    string DisplayName,
    bool MustChangePassword,
    bool IsActive,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<int> ZoneIds);

/// <summary>
/// Respuesta de POST /auth/login: token web (scope=web) + claims materializados.
/// </summary>
public sealed record WebLoginResponse(
    string AccessToken,
    string ExpiresAt,
    WebUserSessionDto User,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<int> ZoneIds,
    string Scope = "web");