using System.Threading;
using System.Threading.Tasks;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public interface ITelemetryService
{
    Task<TelemetryEventResponse> ProcessEventAsync(TelemetryEventRequest request, int sellerId, CancellationToken ct = default);
}
