using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http.Headers;
using System.Text;

namespace Rutx.Sincronizador.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AdminAuthAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var user = config["WebAuth:AdminUsername"];
        var pass = config["WebAuth:AdminPassword"];

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            context.Result = new UnauthorizedObjectResult(new { message = "Credenciales administrativas no configuradas." });
            return;
        }

        try
        {
            var authString = context.HttpContext.Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authString)) throw new Exception("No auth header");
            var authHeader = AuthenticationHeaderValue.Parse(authString);
            var credentialBytes = Convert.FromBase64String(authHeader.Parameter ?? "");
            var credentials = Encoding.UTF8.GetString(credentialBytes).Split(':', 2);
            if (credentials.Length == 2 && credentials[0] == user && credentials[1] == pass)
            {
                return; // Authorized
            }
        }
        catch
        {
            // Parse failed, fall through to Unauthorized
        }

        context.HttpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"RutX Admin Panel\"";
        context.Result = new UnauthorizedResult();
    }
}
