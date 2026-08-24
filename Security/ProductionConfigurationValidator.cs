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
                fbConn.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection no puede estar vacia ni contener placeholders (CHANGE_ME) en Produccion.");
            }

            try
            {
                var builder = new FbConnectionStringBuilder(fbConn);
                if (string.IsNullOrWhiteSpace(builder.Password))
                {
                    throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection debe incluir una contrasena valida (Password no puede estar vacia).");
                }

                if (builder.Password.Equals("masterkey", StringComparison.OrdinalIgnoreCase))
                {
                    bool esLocal = string.IsNullOrWhiteSpace(builder.DataSource) || 
                                   builder.DataSource.Equals("localhost", StringComparison.OrdinalIgnoreCase) || 
                                   builder.DataSource.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);

                    if (esLocal)
                    {
                        Console.Error.WriteLine("[SEGURIDAD - AVISO] Firebird usa la contrasena por defecto ('masterkey') en localhost. Se recomienda cambiarla para mayor seguridad.");
                    }
                    else
                    {
                        throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: FirebirdConnection no puede usar contrasenas por defecto ('masterkey') en servidores remotos.");
                    }
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

            // 3b. Contraseñas debiles o placeholder: rechazo SIEMPRE en Produccion,
            // independientemente de si la API externa (Cloudflare Tunnel) esta activa.
            if (string.Equals(adminUser, adminPass, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: WebAuth:AdminPassword no puede ser identica al usuario en Produccion.");
            }
            if (adminPass.Equals("admin", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: WebAuth:AdminPassword no puede ser 'admin' en Produccion.");
            }
            if (adminPass.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: WebAuth:AdminPassword contiene el placeholder CHANGE_ME en Produccion.");
            }
            if (adminPass.Length < 12)
            {
                throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: WebAuth:AdminPassword debe tener al menos 12 caracteres en Produccion.");
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

                // 4b. AllowedHosts estricto cuando la API externa (tunel) esta activa:
                // obligatorio, sin '*', sin placeholders, con al menos un hostname
                // publico y al menos un host local.
                var allowedHosts = configuration["AllowedHosts"];
                if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Trim() == "*")
                {
                    throw new InvalidOperationException(
                        "[SEGURIDAD] Arranque abortado: Network:ExternalApiEnabled activo exige AllowedHosts explicito " +
                        "(hostname real de Cloudflare + localhost/127.0.0.1). Valor actual: '" +
                        (allowedHosts ?? "(vacío)") + "'.");
                }

                var segmentos = allowedHosts
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                if (segmentos.Count == 0)
                {
                    throw new InvalidOperationException("[SEGURIDAD] Arranque abortado: AllowedHosts no contiene segmentos validos.");
                }

                foreach (var seg in segmentos)
                {
                    if (seg.Contains('<') || seg.Contains('>') ||
                        seg.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                        seg.EndsWith("example.com", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"[SEGURIDAD] Arranque abortado: AllowedHosts contiene placeholder o valor no real ('{seg}') en Produccion.");
                    }
                }

                var tieneHostnamePublico = segmentos.Any(s =>
                    !s.Equals("localhost", StringComparison.OrdinalIgnoreCase) && s != "127.0.0.1");
                var tieneHostLocal = segmentos.Any(s =>
                    s.Equals("localhost", StringComparison.OrdinalIgnoreCase) || s == "127.0.0.1");

                if (!tieneHostnamePublico || !tieneHostLocal)
                {
                    throw new InvalidOperationException(
                        "[SEGURIDAD] Arranque abortado: AllowedHosts debe incluir el hostname real de Cloudflare " +
                        "Y localhost o 127.0.0.1 (ejemplo: 'sync.cliente.com;localhost;127.0.0.1').");
                }

                // 4c. Cloudflare:PublicHostname es OBLIGATORIO con API externa activa y
                // debe aparecer exactamente en AllowedHosts: valida contra el hostname
                // real del tunel, no solo sintaxis.
                var publicHostname = configuration["Cloudflare:PublicHostname"];
                if (string.IsNullOrWhiteSpace(publicHostname))
                {
                    throw new InvalidOperationException(
                        "[SEGURIDAD] Arranque abortado: Network:ExternalApiEnabled activo exige Cloudflare:PublicHostname " +
                        "con el hostname publico real del tunel (variable Cloudflare__PublicHostname).");
                }
                if (publicHostname.Contains('<') || publicHostname.Contains('>') ||
                    publicHostname.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "[SEGURIDAD] Arranque abortado: Cloudflare:PublicHostname contiene un placeholder; configure el hostname real.");
                }
                if (!segmentos.Contains(publicHostname.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"[SEGURIDAD] Arranque abortado: Cloudflare:PublicHostname ('{publicHostname.Trim()}') debe estar incluido en AllowedHosts.");
                }
            }
        }
    }
}
