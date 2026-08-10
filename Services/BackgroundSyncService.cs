using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Services;

public class BackgroundSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundSyncService> _logger;
    private readonly int _intervaloSegundos;

    public BackgroundSyncService(
        IServiceScopeFactory scopeFactory,
        ILogger<BackgroundSyncService> logger,
        int intervaloSegundos = 5)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _intervaloSegundos = intervaloSegundos;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundSyncService iniciado. Intervalo: {Intervalo}s", _intervaloSegundos);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var colaService = scope.ServiceProvider.GetRequiredService<IColaOfflineService>();

                var pendientes = await colaService.ObtenerPendientesAsync(limite: 10);

                if (pendientes.Count > 0)
                {
                    _logger.LogInformation("Procesando {Count} operaciones pendientes", pendientes.Count);

                    foreach (var operacion in pendientes)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        _logger.LogInformation(
                            "Procesando operación {OperacionId} (tipo: {Tipo}, intentos: {Intentos}/{MaxIntentos})",
                            operacion.OperacionId, operacion.TipoOperacion, operacion.Intentos, operacion.MaxIntentos);

                        await colaService.ProcesarOperacionAsync(operacion);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo de BackgroundSyncService");
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervaloSegundos), stoppingToken);
        }

        _logger.LogInformation("BackgroundSyncService detenido");
    }
}