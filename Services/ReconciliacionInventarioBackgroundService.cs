// ============================================================================
// ARCHIVO: ReconciliacionInventarioBackgroundService.cs
// PROPOSITO: Ejecuta la reconciliacion de movimientos de inventario cada N
// minutos, registra las anomalias en el log y deja el ultimo resultado
// disponible para GET /api/v1/inventario/reconciliacion/ultima.
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public class ReconciliacionInventarioBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReconciliacionInventarioBackgroundService> _logger;
    private readonly int _intervaloMinutos;
    private readonly int _ventanaDias;

    public ReconciliacionInventarioBackgroundService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ReconciliacionInventarioBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _intervaloMinutos = configuration.GetValue<int>("InventarioReconciliacion:IntervaloMinutos", 15);
        _ventanaDias = configuration.GetValue<int>("InventarioReconciliacion:VentanaDias", 7);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[Inventario] Reconciliacion programada cada {Intervalo} min (ventana {Ventana} días).",
            _intervaloMinutos, _ventanaDias);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EjecutarReconciliacionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Inventario] Error en la corrida de reconciliacion");
            }

            await Task.Delay(TimeSpan.FromMinutes(_intervaloMinutos), stoppingToken);
        }
    }

    private async Task EjecutarReconciliacionAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IInventarioReconciliacionService>();

        var desde = DateTime.Today.AddDays(-_ventanaDias);
        var resultado = await service.ReconciliarAsync(desde);

        if (resultado.Anomalias > 0)
        {
            _logger.LogWarning(
                "[Inventario] Reconciliacion: {Transfer} traspasos, {Seed} saldos iniciales, {Anomalias} ANOMALIAS.",
                resultado.Transferencias, resultado.Semillas, resultado.Anomalias);

            foreach (var anomalia in resultado.AnomaliasList)
            {
                _logger.LogWarning(
                    "[Inventario] ANOMALIA DOCTO={Docto} art={ArticuloId} ({Articulo}) unds={Unidades} " +
                    "origen={Origen} destino={Destino} concepto={Concepto}: {Motivo}",
                    anomalia.DoctoInId, anomalia.ArticuloId, anomalia.ArticuloNombre,
                    anomalia.Unidades, anomalia.AlmacenOrigenId, anomalia.AlmacenDestinoId,
                    anomalia.ConceptoNombre, anomalia.Motivo);
            }
        }
        else
        {
            _logger.LogInformation(
                "[Inventario] Reconciliacion OK: {Transfer} traspasos, {Seed} saldos iniciales, sin anomalias.",
                resultado.Transferencias, resultado.Semillas);
        }
    }
}
