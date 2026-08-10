using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Serializa la reserva de folios POR CAJA dentro del proceso, usando un
/// SemaphoreSlim por CAJA_ID. Esto elimina los deadlocks entre operaciones
/// concurrentes del propio sincronizador sobre la misma caja (ej: dos ventas
/// simultáneas de la misma caja offline).
///
/// El timeout evita que una caja "colgada" bloquee para siempre las demás.
/// Configuración: MicrosipSettings:FolioLockTimeoutSeconds (default 120).
/// </summary>
public interface IFolioLockService
{
    /// <summary>
    /// Adquiere el candado de la caja. Devuelve un handle que se debe liberar
    /// con DisposeAsync (preferentemente con `await using`).
    /// Lanza FolioOcupadoException si el timeout se agota.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(int cajaId);
}

public class FolioLockService : IFolioLockService
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semafores = new();
    private readonly TimeSpan _timeout;
    private readonly ILogger<FolioLockService> _logger;

    public FolioLockService(IConfiguration configuration, ILogger<FolioLockService> logger)
    {
        int timeoutSegundos = configuration.GetValue<int>("MicrosipSettings:FolioLockTimeoutSeconds", 120);
        _timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSegundos));
        _logger = logger;
    }

    public async Task<IAsyncDisposable> AcquireAsync(int cajaId)
    {
        if (cajaId <= 0)
            throw new ArgumentException("No se pudo resolver la caja del usuario. Verifica que el cajero tenga una caja asignada.");

        var semaforo = _semafores.GetOrAdd(cajaId, _ => new SemaphoreSlim(1, 1));

        bool adquirido = await semaforo.WaitAsync(_timeout);
        if (!adquirido)
        {
            _logger.LogWarning("Caja {CajaId}: timeout de {Segundos}s agotado esperando candado de folios.", cajaId, _timeout.TotalSeconds);
            throw new FolioOcupadoException(
                $"La caja está procesando otra operación y no respondió a tiempo. Intenta de nuevo en unos momentos.");
        }

        return new Candado(cajaId, semaforo);
    }

    private sealed class Candado : IAsyncDisposable
    {
        private readonly int _cajaId;
        private readonly SemaphoreSlim _semaforo;
        private int _liberado;

        public Candado(int cajaId, SemaphoreSlim semaforo)
        {
            _cajaId = cajaId;
            _semaforo = semaforo;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                _semaforo.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
