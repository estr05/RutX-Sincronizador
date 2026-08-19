using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System;
using FirebirdSql.Data.FirebirdClient;

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

            var expiration = configuration.GetValue<int>("Jwt:ExpirationMinutes", 480);
            if (expiration < 15 || expiration > 480)
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: Jwt:ExpirationMinutes debe estar entre 15 y 480 minutos (8 horas) en Produccion.");
            }

            // 2. Validacion de Firebird
            var fbConn = configuration.GetConnectionString("FirebirdConnection");
            if (string.IsNullOrWhiteSpace(fbConn) || 
                fbConn.Contains("masterkey", StringComparison.OrdinalIgnoreCase) || 
                fbConn.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection no puede estar vacia, ni usar contrasenas por defecto ('masterkey') o placeholders (CHANGE_ME) en Produccion.");
            }

            try
            {
                var builder = new FbConnectionStringBuilder(fbConn);
                if (string.IsNullOrWhiteSpace(builder.Password) || 
                    builder.Password.Equals("masterkey", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection debe incluir una contrasena valida (Password no puede estar vacia ni ser 'masterkey').");
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: No se pudo parsear FirebirdConnection.", ex);
            }

            // 3. Validacion de Admin Auth (Basic)
            var adminUser = configuration["WebAuth:AdminUsername"];
            var adminPass = configuration["WebAuth:AdminPassword"];
            if (string.IsNullOrWhiteSpace(adminUser) || string.IsNullOrWhiteSpace(adminPass))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: WebAuth:AdminUsername y AdminPassword deben estar configurados en Produccion para el panel local.");
            }

            // 4. Validacion TLS y Topologia para puertos expuestos
            var externalEnabled = configuration.GetValue<bool>("Network:ExternalApiEnabled");
            if (externalEnabled)
            {
                var mode = configuration["Network:ExternalApiMode"];
                if (string.IsNullOrWhiteSpace(mode) || (mode != "ReverseProxy" && mode != "KestrelHttps" && mode != "VPN_Directo"))
                {
                    throw new InvalidOperationException($"[SEGURIDAD] Arranque abortado: Network:ExternalApiEnabled esta activo, pero el modo ('{mode}') no es valido para produccion. Use ReverseProxy, KestrelHttps o VPN_Directo.");
                }

                // En modo KestrelHttps, nos aseguramos de que Kestrel:Endpoints:Https este presente para proveer el certificado
                if (mode == "KestrelHttps" && configuration.GetSection("Kestrel:Endpoints:Https").Exists() == false)
                {
                    throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: Modo KestrelHttps habilitado pero no se ha provisto configuracion de Kestrel:Endpoints:Https (falta certificado).");
                }
            }
        }
    }
}
