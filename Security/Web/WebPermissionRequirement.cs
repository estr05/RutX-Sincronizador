using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Security.Web;

/// <summary>
/// Requisito de autorización por permiso del portal: el token debe traer
/// scope=web y el permiso solicitado en los claims "permissions".
/// Política registrada en Program.cs:  web.{permiso}  →  WebPermissionRequirement.
/// </summary>
public sealed class WebPermissionRequirement : IAuthorizationRequirement
{
    public string Permiso { get; }

    public WebPermissionRequirement(string permiso) => Permiso = permiso;

    public const string ScopeClaim = "scope";
    public const string ScopeWeb = "web";
    public const string PermissionsClaim = "permissions";

    /// <summary>JwtBearer mapea el claim "scope" del JWT a este tipo al validar.</summary>
    public const string ScopeMappedClaim = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>Lee el claim de scope tolerando el mapeo de entrada de JwtBearer.</summary>
    public static string? ObtenerScope(ClaimsPrincipal principal)
        => principal.FindFirst(ScopeClaim)?.Value
           ?? principal.FindFirst(ScopeMappedClaim)?.Value;
}

/// <summary>
/// Handler del requisito. La presencia de un permiso en el claim NO depende
/// del nombre del rol: los permisos se resuelven en el login vía
/// WebRoleCatalog (única fuente rol → permisos) y se materializan en el token.
/// Un requisito sin permiso ("web.any", identidad me/logout) solo exige scope=web.
/// </summary>
public sealed class WebPermissionHandler : AuthorizationHandler<WebPermissionRequirement>
{
    private readonly ILogger<WebPermissionHandler> _logger;

    public WebPermissionHandler(ILogger<WebPermissionHandler> logger) => _logger = logger;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        WebPermissionRequirement requirement)
    {
        _logger.LogInformation("Handler invocado para permiso {Permiso} (scope={Scope})",
            requirement.Permiso, WebPermissionRequirement.ObtenerScope(context.User) ?? "(sin scope)");

        var scope = WebPermissionRequirement.ObtenerScope(context.User);
        if (scope != WebPermissionRequirement.ScopeWeb)
        {
            _logger.LogWarning("Permiso {Permiso}: scope={Scope}, claims={Claims}",
                requirement.Permiso, scope ?? "(sin scope)",
                string.Join("|", context.User.Claims.Select(c => $"{c.Type}={c.Value}")));
            return Task.CompletedTask;
        }

        if (string.IsNullOrEmpty(requirement.Permiso))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var tienePermiso = context.User
            .FindAll(WebPermissionRequirement.PermissionsClaim)
            .Any(c => c.Value == requirement.Permiso);

        if (tienePermiso)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}