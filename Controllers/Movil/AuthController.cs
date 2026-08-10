using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IFirebirdAuthService _authService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IFirebirdAuthService authService,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _configuration = configuration;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Autentica al usuario contra Firebird (credenciales nativas de Microsip).
    ///
    /// NO se usa la tabla AGENTES. El usuario y password se validan abriendo
    /// una conexion de prueba a la base; si Firebird las rechaza -> 401.
    ///
    /// Con la identidad Firebird se resuelve la ruta del usuario:
    ///   - vendedor_id  : VENDEDORES.NOMBRE = usuario
    ///   - cajero_id    : CAJEROS.USUARIO = usuario
    ///   - caja_id      : CAJAS_CAJEROS / evidencia (MOVTOS_CAJAS, DOCTOS_PV)
    ///   - almacen_id   : CAJAS.ALMACEN_ID
    ///
    /// RESPUESTA:
    ///   - token: JWT con vendedor_id, cajero_id, caja_id y almacen_id
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Usuario) ||
            string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { mensaje = "Usuario y contraseña son requeridos." });

        try
        {
            var sesion = await _authService.AutenticarAsync(
                request.Usuario.Trim(),
                request.Password);

            if (sesion == null)
            {
                _logger.LogWarning(
                    "Intento de login fallido para usuario '{Usuario}' desde {IP}",
                    request.Usuario,
                    HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { mensaje = "Usuario o contraseña incorrectos." });
            }

            var token = GenerarToken(sesion);

            return Ok(new
            {
                token,
                vendedor_id = sesion.VendedorId,
                vendedor_nombre = sesion.VendedorNombre,
                usuario = sesion.Usuario,
                cajero_id = sesion.CajeroId,
                caja_id = sesion.CajaId,
                almacen_id = sesion.AlmacenId,
                sucursal_id = sesion.SucursalId
            });
        }
        catch (RutaAuthException ex)
        {
            _logger.LogWarning("Login sin permiso para usuario '{Usuario}': {Mensaje}",
                request.Usuario, ex.Message);
            return StatusCode(403, new { mensaje = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al autenticar usuario '{Usuario}'", request.Usuario);
            return StatusCode(500, new
            {
                mensaje = "No se pudo conectar con la base de datos. Verifica que el servidor este disponible."
            });
        }
    }

    /// <summary>
    /// Devuelve la identidad de ruta del usuario autenticado desde el JWT.
    /// Requiere Authorization: Bearer {token}
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        var usuario = User.FindFirst(ClaimTypes.Name)?.Value ?? "";
        var vendedorId = User.FindFirst("vendedor_id")?.Value;
        var cajeroId = User.FindFirst("cajero_id")?.Value;
        var cajaId = User.FindFirst("caja_id")?.Value;
        var almacenId = User.FindFirst("almacen_id")?.Value;
        var sucursalId = User.FindFirst("sucursal_id")?.Value;

        if (string.IsNullOrEmpty(vendedorId) || !int.TryParse(vendedorId, out int vendedorIdInt))
            return Unauthorized(new { mensaje = "Token invalido: no se encontro vendedor_id." });

        return Ok(new
        {
            vendedor_id = vendedorIdInt,
            vendedor_nombre = User.FindFirst("vendedor_nombre")?.Value ?? usuario,
            usuario,
            cajero_id = ParseEntero(cajeroId),
            caja_id = ParseEntero(cajaId),
            almacen_id = ParseEntero(almacenId),
            sucursal_id = ParseEntero(sucursalId),
            ruta = (object?)null
        });
    }

    private string GenerarToken(UsuarioSesion sesion)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, sesion.Usuario),
            new Claim("usuario", sesion.Usuario),
            new Claim("rol", "vendedor"),
            new Claim("vendedor_id", sesion.VendedorId.ToString()),
            new Claim("vendedor_nombre", sesion.VendedorNombre),
            new Claim("cajero_id", sesion.CajeroId.ToString()),
            new Claim("caja_id", sesion.CajaId.ToString()),
            new Claim("almacen_id", sesion.AlmacenId.ToString()),
            new Claim("sucursal_id", sesion.SucursalId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static int ParseEntero(string? valor)
    {
        return int.TryParse(valor, out int result) ? result : 0;
    }
}

public record LoginRequest(string Usuario, string Password);
