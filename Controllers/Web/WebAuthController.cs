using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Rutx.Sincronizador.Models.Web.Auth;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Autenticación del portal web (contrato v2 §5).
/// - POST /api/v2/web/auth/login   → público, rate limit 5/min por IP
///                                   (política "web-login" registrada en Program.cs).
/// - GET  /api/v2/web/auth/me      → requiere Bearer scope=web.
/// - POST /api/v2/web/auth/logout  → requiere Bearer; 204, revocación
///                                   remota de tokens es posterior en el contrato.
/// Los errores de credenciales NUNCA revelan si el usuario existe.
/// </summary>
[ApiController]
[Route("api/v2/web/auth")]
public class WebAuthController : ControllerBase
{
    private readonly IWebAuthService _authService;
    private readonly ILogger<WebAuthController> _logger;

    public WebAuthController(IWebAuthService authService, ILogger<WebAuthController> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("web-login")]
    public async Task<IActionResult> Login([FromBody] WebLoginRequest request, CancellationToken ct)
    {
        var traceId = HttpContext.Items["trace_id"]?.ToString();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        var resultado = await _authService.LoginAsync(request, ip, traceId, ct);

        if (!resultado.IsSuccess)
        {
            var status = resultado.Code switch
            {
                "ACCOUNT_DISABLED" => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status401Unauthorized,
            };
            return StatusCode(status, WebEnvelope.Error(resultado.Code!, resultado.Message!));
        }

        _logger.LogInformation("Login web exitoso para '{Username}' desde {IP}", request.Username?.Trim(), ip);
        return Ok(WebEnvelope.Success(resultado.Response!));
    }

    [HttpGet("me")]
    [Authorize(Policy = "web.any")]
    public IActionResult Me()
    {
        var resultado = _authService.Me(User);

        if (!resultado.IsSuccess)
            return Unauthorized(WebEnvelope.Error(resultado.Code!, resultado.Message!));

        return Ok(WebEnvelope.Success(resultado.Response!));
    }

    [HttpPost("logout")]
    [Authorize(Policy = "web.any")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var username = User.FindFirst("username")?.Value;
        var userId = long.TryParse(User.FindFirst("user_id")?.Value, out var id) ? id : (long?)null;
        var traceId = HttpContext.Items["trace_id"]?.ToString();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        await _authService.LogoutAuditAsync(userId, username, ip, traceId, ct);
        return NoContent();
    }
}