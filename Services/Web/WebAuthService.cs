using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models.Web.Auth;
using Rutx.Sincronizador.Security;
using Rutx.Sincronizador.Security.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Implementación de IWebAuthService sobre WebSqliteStore + WebPasswordHasher.
/// El rehash en el login (NecesitaRehash) rota el hash al estándar vigente
/// sin tocar la credencial; el auditorio registra cada intento.
/// </summary>
public sealed class WebAuthService : IWebAuthService
{
    private readonly IWebSqliteStore _store;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebAuthService> _logger;

    public WebAuthService(IWebSqliteStore store, IConfiguration configuration, ILogger<WebAuthService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configuration = configuration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WebAuthResult> LoginAsync(
        WebLoginRequest request,
        string? ipAddress,
        string? traceId,
        CancellationToken cancellationToken = default)
    {
        var username = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(request.Password))
            return WebAuthResult.Error("VALIDATION_ERROR", "Usuario y contraseña son requeridos.");

        var usuario = await _store.FindUserByUsernameAsync(username, cancellationToken);

        if (usuario == null || !WebPasswordHasher.Verify(request.Password, usuario.PasswordHash))
        {
            _logger.LogWarning("Login web fallido para '{Username}' desde {IP}", username, ipAddress);
            await _store.LogAuditAsync(null, username, "auth.login", "fallo credenciales", ipAddress, traceId, cancellationToken);
            return WebAuthResult.Error("UNAUTHENTICATED", "Usuario o contraseña incorrectos.");
        }

        if (!usuario.IsActive)
        {
            await _store.LogAuditAsync(usuario.Id, usuario.Username, "auth.login", "cuenta deshabilitada", ipAddress, traceId, cancellationToken);
            return WebAuthResult.Error("ACCOUNT_DISABLED", "Cuenta deshabilitada. Contacta al administrador.");
        }

        if (WebPasswordHasher.NecesitaRehash(usuario.PasswordHash))
        {
            await _store.UpdateUserPasswordAsync(usuario.Id, WebPasswordHasher.Hash(request.Password), cancellationToken);
            _logger.LogInformation("Login web: hash rotado para '{Username}' (política de iteraciones).", username);
        }

        var roles = DeserializarRoles(usuario.RolesJson);
        var permisos = WebRoleCatalog.ResolverPermisos(roles);
        var zonas = DeserializarZonas(usuario.ZoneIdsJson);

        var sesion = new WebUserSessionDto(
            usuario.Id,
            usuario.Username,
            usuario.DisplayName,
            usuario.MustChangePassword,
            true,
            roles,
            permisos,
            zonas);

        var token = WebTokenFactory.Emitir(_configuration, sesion);

        await _store.LogAuditAsync(usuario.Id, usuario.Username, "auth.login", "exitoso", ipAddress, traceId, cancellationToken);

        return WebAuthResult.Exito(new WebLoginResponse(
            token,
            DateTime.UtcNow.AddHours(WebTokenFactory.VigenciaHoras).ToString("o"),
            sesion,
            roles,
            permisos,
            zonas));
    }

    public WebAuthResult Me(ClaimsPrincipal principal)
    {
        var userId = principal.FindFirst("user_id")?.Value;
        var username = principal.FindFirst("username")?.Value;
        var displayName = principal.FindFirst("display_name")?.Value ?? username ?? string.Empty;
        var roles = principal.FindAll("roles").Select(c => c.Value).ToArray();
        var permisos = principal.FindAll("permissions").Select(c => c.Value).ToArray();
        var zonas = principal.FindAll("zone_ids").Select(c => int.TryParse(c.Value, out var z) ? z : -1)
            .Where(z => z >= 0).ToArray();

        if (string.IsNullOrWhiteSpace(username) || !long.TryParse(userId, out var id))
            return WebAuthResult.Error("UNAUTHENTICATED", "Token inválido: identidad incompleta.");

        var sesion = new WebUserSessionDto(id, username, displayName, false, true, roles, permisos, zonas);
        return WebAuthResult.Exito(new WebLoginResponse(string.Empty, string.Empty, sesion, roles, permisos, zonas));
    }

    public async Task LogoutAuditAsync(long? userId, string? username, string? ipAddress, string? traceId, CancellationToken cancellationToken = default)
    {
        await _store.LogAuditAsync(userId, username, "auth.logout", "cierre de sesion", ipAddress, traceId, cancellationToken);
        _logger.LogInformation("Logout web de '{Username}'", username);
    }

    private static string[] DeserializarRoles(string rolesJson)
    {
        try
        {
            var roles = JsonSerializer.Deserialize<string[]>(rolesJson);
            return roles is { Length: > 0 }
                ? roles.Where(WebRoleCatalog.EsRolHabilitado).ToArray()
                : Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static int[] DeserializarZonas(string zoneIdsJson)
    {
        try
        {
            var zonas = JsonSerializer.Deserialize<int[]>(zoneIdsJson);
            return zonas is { Length: > 0 } ? zonas : Array.Empty<int>();
        }
        catch (JsonException)
        {
            return Array.Empty<int>();
        }
    }
}