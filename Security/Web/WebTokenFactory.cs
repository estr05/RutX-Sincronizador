using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Rutx.Sincronizador.Models.Web.Auth;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Security.Web;

/// <summary>
/// Emisión del JWT del portal (scope=web). Espejo del patrón móvil
/// (HmacSha256, 12 h) con los claims del contrato v2 §5: identidad,
/// roles, permisos resueltos y zonas autorizadas. scope=web impide que
/// un token del portal sea aceptado en los endpoints móviles (la
/// autorización por permiso exige el claim scope).
/// </summary>
public static class WebTokenFactory
{
    public const string ScopeWeb = "web";
    public const int VigenciaHoras = 12;

    public static string Emitir(IConfiguration configuration, WebUserSessionDto sesion)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, sesion.Username),
            new("user_id", sesion.Id.ToString()),
            new("username", sesion.Username),
            new("display_name", sesion.DisplayName),
            new("scope", ScopeWeb),
        };
        foreach (var rol in sesion.Roles)
            claims.Add(new Claim("roles", rol));
        foreach (var permiso in sesion.Permissions)
            claims.Add(new Claim("permissions", permiso));
        foreach (var zona in sesion.ZoneIds)
            claims.Add(new Claim("zone_ids", zona.ToString()));

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(VigenciaHoras),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}