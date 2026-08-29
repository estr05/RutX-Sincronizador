using System.Threading;
using System.Threading.Tasks;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public interface IDeviceRegistryService
{
    Task<DeviceActivationResponse> ActivateDeviceAsync(DeviceActivationRequest request, int sellerId, CancellationToken ct = default);
}
