// ----------------------------------------------------------------
// Topologia de listeners de Kestrel.
//
// Invariantes (verificados en Tests/Unit/KestrelTopologyTests.cs):
//   - Development: 5047 en AnyIP (0.0.0.0) para conservar el acceso
//     directo LAN/Tailscale de la app movil mientras conviva el modo
//     legacy. En Production: 5047 SIEMPRE en loopback estricto.
//   - Si Network:ExternalApiEnabled=true:
//       ReverseProxy -> listener adicional loopback (127.0.0.1) en
//                      ExternalPort (default 5048), destinado a recibir
//                      SOLO el trafico del tunel de cloudflared.
//       KestrelHttps -> listener externo con TLS gestionado por Kestrel
//                       (requiere configuracion Kestrel:Endpoints:Https).
//       VPN_Directo  -> listener externo HTTP sin terminacion TLS local.
//   - Modo invalido/indefinido con API externa activa: aborta arranque.
// ----------------------------------------------------------------
namespace Rutx.Sincronizador;

public sealed record ListenerEndpoint(bool LoopbackOnly, int Port, bool UseHttps);

public static class KestrelTopology
{
    public const int PuertoAdministrativo = 5047;
    public const int PuertoExternoDefault = 5048;

    public static IReadOnlyList<ListenerEndpoint> ResolveListeners(
        bool esProduction,
        bool externalEnabled,
        string? mode,
        int externalPort)
    {
        var list = new List<ListenerEndpoint>
        {
            // Production => loopback estricto (solo administracion local);
            // Development => AnyIP para conservar LAN/Tailscale.
            new(esProduction, PuertoAdministrativo, false)
        };

        if (!externalEnabled)
        {
            return list;
        }

        var puerto = externalPort > 0 ? externalPort : PuertoExternoDefault;
        switch (mode)
        {
            case "ReverseProxy":
                list.Add(new ListenerEndpoint(true, puerto, false));
                break;
            case "KestrelHttps":
                list.Add(new ListenerEndpoint(false, puerto, true));
                break;
            case "VPN_Directo":
                list.Add(new ListenerEndpoint(false, puerto, false));
                break;
            default:
                throw new InvalidOperationException(
                    $"[SEGURIDAD] Network:ExternalApiMode no soportado o indefinido ('{mode}'). " +
                    "Opciones: ReverseProxy, KestrelHttps, VPN_Directo.");
        }

        return list;
    }
}
