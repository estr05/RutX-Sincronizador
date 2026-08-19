using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;

namespace Rutx.Sincronizador.Security
{
    public static class ProductionConfigurationValidator
    {
        /// <summary>
        /// Verifica que no existan secretos vulnerables ni configuraciones de red inseguras
        /// cuando la aplicacion se arranca en entorno de produccion.
        /// </summary>
        public static void Validate(IConfiguration configuration, IHostEnvironment environment)
        {
            if (!environment.IsProduction()) return;

            // 1. Validacion de JWT
            var jwtKey = configuration["Jwt:Key"];
            if (string.IsNullOrWhiteSpace(jwtKey) || 
                jwtKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) || 
                jwtKey.Length < 32)
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: Jwt:Key es invalida, vacia, menor a 32 caracteres o contiene el placeholder CHANGE_ME en Produccion.");
            }

            // 2. Validacion de Firebird
            var fbConn = configuration.GetConnectionString("FirebirdConnection");
            if (string.IsNullOrWhiteSpace(fbConn) || 
                fbConn.Contains("masterkey", StringComparison.OrdinalIgnoreCase) || 
                fbConn.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection no puede estar vacia, ni usar contrasenas por defecto ('masterkey') o placeholders (CHANGE_ME) en Produccion.");
            }

            // 3. Validacion TLS para endpoints no locales
            var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? configuration["ASPNETCORE_URLS"] ?? string.Empty;
            
            // Si hay listeners configurados y ninguno incluye https, pero estan en interfaces abiertas, abortar
            if (!string.IsNullOrWhiteSpace(urls) && !urls.Contains("https://", StringComparison.OrdinalIgnoreCase) && !urls.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !urls.Contains("127.0.0.1"))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: Se ha detectado una configuracion de red insegura. ASPNETCORE_URLS expone la API publicamente sin configuracion TLS/HTTPS.");
            }
        }
    }
}
