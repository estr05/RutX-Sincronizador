using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public class ColaOfflineService : IColaOfflineService
{
    private readonly IColaOfflineRepository _repository;
    private readonly IVentaServicePv _ventaServicePv;
    private readonly IClienteService _clienteService;
    private readonly ILogger<ColaOfflineService> _logger;
    private readonly int _maxIntentos;
    private readonly int _baseDelaySegundos;
    private readonly int _maxDelaySegundos;

    public ColaOfflineService(
        IColaOfflineRepository repository,
        IVentaServicePv ventaServicePv,
        IClienteService clienteService,
        ILogger<ColaOfflineService> logger,
        int maxIntentos = 5,
        int baseDelaySegundos = 1,
        int maxDelaySegundos = 30)
    {
        _repository = repository;
        _ventaServicePv = ventaServicePv;
        _clienteService = clienteService;
        _logger = logger;
        _maxIntentos = maxIntentos;
        _baseDelaySegundos = baseDelaySegundos;
        _maxDelaySegundos = maxDelaySegundos;
    }

    public async Task<ColaOperacion> AgregarOperacionAsync(TipoOperacion tipo, string idempotencia, object payload)
    {
        var existente = await _repository.ObtenerPorIdempotenciaAsync(tipo, idempotencia);
        if (existente != null)
            return existente;

        var operacion = new ColaOperacion
        {
            OperacionId = Guid.NewGuid().ToString(),
            TipoOperacion = tipo,
            Payload = JsonSerializer.Serialize(payload),
            Estado = EstadoOperacion.PENDIENTE,
            Intentos = 0,
            MaxIntentos = _maxIntentos,
            SiguienteReintento = DateTime.UtcNow
        };

        return await _repository.InsertarAsync(operacion);
    }

    public async Task<ColaOperacion?> ObtenerEstadoAsync(string operacionId)
    {
        return await _repository.ObtenerPorIdAsync(operacionId);
    }

    public async Task<List<ColaOperacion>> ObtenerPendientesAsync(int limite = 10)
    {
        return await _repository.ObtenerPendientesAsync(limite);
    }

    public async Task<List<ColaOperacion>> ObtenerPorEstadoAsync(EstadoOperacion estado, int limite = 100)
    {
        return await _repository.ObtenerPorEstadoAsync(estado, limite);
    }

    public async Task ProcesarOperacionAsync(ColaOperacion operacion)
    {
        operacion.Estado = EstadoOperacion.PROCESANDO;
        await _repository.ActualizarAsync(operacion);

        // Heartbeat: mientras el procesamiento corre, refresca FechaModificacion
        // cada 20 s para que ObtenerPendientesAsync NO libere esta operacion como
        // "colgada" si tarda mas de 2 min (Firebird lento + retries con backoff).
        // Sin esto, el proceso en curso se reprocesaria en paralelo y se podria
        // duplicar la venta/cobro con un folio distinto.
        using var cts = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(operacion.OperacionId, cts.Token);

        try
        {
            switch (operacion.TipoOperacion)
            {
                case TipoOperacion.VENTA:
                    await ProcesarVentaAsync(operacion);
                    break;
                case TipoOperacion.CLIENTE:
                    await ProcesarClienteAsync(operacion);
                    break;
                case TipoOperacion.CIERRE:
                    await ProcesarCierreAsync(operacion);
                    break;
            }

            operacion.Estado = EstadoOperacion.COMPLETADO;
            operacion.ErrorUltimoIntento = null;
            await _repository.ActualizarAsync(operacion);

            _logger.LogInformation("Operación {OperacionId} completada exitosamente", operacion.OperacionId);
        }
        catch (Exception ex)
        {
            operacion.Intentos++;
            operacion.ErrorUltimoIntento = ex.Message;

            if (operacion.Intentos >= operacion.MaxIntentos)
            {
                operacion.Estado = EstadoOperacion.FALLIDO;
                _logger.LogError(ex, "Operación {OperacionId} marcada como FALLIDO después de {Intentos} intentos",
                    operacion.OperacionId, operacion.Intentos);
            }
            else
            {
                operacion.Estado = EstadoOperacion.PENDIENTE;
                operacion.SiguienteReintento = CalcularSiguienteReintento(operacion.Intentos);
                _logger.LogWarning(ex, "Operación {OperacionId} reprogramada. Intento {Intentos}/{MaxIntentos}, próximo: {Proximo}",
                    operacion.OperacionId, operacion.Intentos, operacion.MaxIntentos, operacion.SiguienteReintento);
            }

            await _repository.ActualizarAsync(operacion);
        }
        finally
        {
            cts.Cancel();
            try { await heartbeat; } catch { /* el heartbeat se detiene con la cancelacion */ }
        }
    }

    /// <summary>
    /// Toca el heartbeat de una operacion en PROCESANDO cada 20 s.
    /// Mantiene FechaModificacion fresca para que la liberacion de "colgadas"
    /// (umbral 2 min) nunca afecte a una operacion que sigue viva.
    /// </summary>
    private async Task HeartbeatAsync(string operacionId, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                await _repository.TocarHeartbeatAsync(operacionId);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelacion normal al terminar el procesamiento
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat fallo para la operación {OperacionId} (se ignora)", operacionId);
        }
    }

    public async Task EliminarAsync(string operacionId)
    {
        await _repository.EliminarAsync(operacionId);
        _logger.LogInformation("Operación {OperacionId} eliminada de la cola", operacionId);
    }

    public async Task<int> ContarPorEstadoAsync(EstadoOperacion estado)
    {
        return await _repository.ContarPorEstadoAsync(estado);
    }

    public DateTime CalcularSiguienteReintento(int intentos)
    {
        var delay = Math.Min(_baseDelaySegundos * Math.Pow(2, intentos), _maxDelaySegundos);
        var jitter = new Random().NextDouble();
        return DateTime.UtcNow.AddSeconds(delay + jitter);
    }

    private async Task ProcesarVentaAsync(ColaOperacion operacion)
    {
        var venta = JsonSerializer.Deserialize<VentaPvCreateDto>(operacion.Payload);
        if (venta == null)
            throw new InvalidOperationException("Payload de venta inválido o vacío");

        if (venta.CajaId == null || venta.CajaId <= 0)
            throw new InvalidOperationException(
                "La venta en cola no tiene CajaId resuelto. El login nativo debe asignar la caja antes de enviar.");

        // La identidad se capturo en el login y viajo en el DTO (cola offline).
        var sesion = new UsuarioSesion
        {
            Usuario = venta.UsuarioCreador ?? "MOVIL",
            VendedorId = venta.VendedorId,
            VendedorNombre = venta.UsuarioCreador ?? "VENDEDOR",
            CajeroId = venta.CajeroId ?? 0,
            CajaId = venta.CajaId.Value,
            AlmacenId = venta.AlmacenId ?? 0,
            SucursalId = venta.SucursalId ?? 0
        };

        await _ventaServicePv.RegistrarVentaPvAsync(sesion, venta);
    }

    private async Task ProcesarClienteAsync(ColaOperacion operacion)
    {
        var cliente = JsonSerializer.Deserialize<ClienteCreateDto>(operacion.Payload);
        if (cliente == null)
            throw new InvalidOperationException("Payload de cliente inválido o vacío");

        await _clienteService.CrearClienteAsync(cliente);
    }

    private async Task ProcesarCierreAsync(ColaOperacion operacion)
    {
        await Task.CompletedTask;
        _logger.LogInformation("Cierre de día procesado (pendiente de implementación real en Firebird)");
    }
}
