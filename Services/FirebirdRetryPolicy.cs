using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;
using FirebirdSql.Data.FirebirdClient;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Reintenta una operación completa de Firebird (incluyendo su transacción)
/// cuando ocurre un conflicto transitorio:
///   - Deadlock (GDSCODE 40001)
///   - Lock conflict (GDSCODE 40002)
///   - Update conflict con otra transacción concurrente (GDSCODE 40003)
///
/// El reintento siempre ejecuta DE NUEVO toda la acción, porque una
/// transacción que entró en deadlock queda marcada para rollback.
/// Configuración: MicrosipSettings:FolioRetryMaxIntentos (default 3),
/// MicrosipSettings:FolioRetryBaseDelayMs (default 500).
/// </summary>
public interface IFirebirdRetryPolicy
{
    Task<T> EjecutarAsync<T>(Func<Task<T>> accion);
    Task EjecutarAsync(Func<Task> accion);
}

public class FirebirdRetryPolicy : IFirebirdRetryPolicy
{
    private readonly int _maxIntentos;
    private readonly int _baseDelayMs;
    private readonly ILogger<FirebirdRetryPolicy> _logger;

    public FirebirdRetryPolicy(IConfiguration configuration, ILogger<FirebirdRetryPolicy> logger)
    {
        _maxIntentos = Math.Max(1, configuration.GetValue<int>("MicrosipSettings:FolioRetryMaxIntentos", 3));
        _baseDelayMs = Math.Max(50, configuration.GetValue<int>("MicrosipSettings:FolioRetryBaseDelayMs", 500));
        _logger = logger;
    }

    public async Task<T> EjecutarAsync<T>(Func<Task<T>> accion)
    {
        for (int intento = 0; ; intento++)
        {
            try
            {
                return await accion();
            }
            catch (Exception ex) when (EsReintentable(ex))
            {
                if (intento >= _maxIntentos - 1)
                {
                    throw new DeadlockTransientException(
                        "El sistema está momentáneamente ocupado y no se pudo completar la operación. Intenta de nuevo.",
                        ex);
                }

                int delay = _baseDelayMs * (1 << intento) + Random.Shared.Next(0, 200);
                _logger.LogWarning(
                    "Conflicto transitorio de Firebird ({Tipo}). Reintento {Intento}/{Max} en {Delay}ms",
                    ex.Message, intento + 1, _maxIntentos, delay);

                await Task.Delay(delay);
            }
        }
    }

    public async Task EjecutarAsync(Func<Task> accion)
    {
        for (int intento = 0; ; intento++)
        {
            try
            {
                await accion();
                return;
            }
            catch (Exception ex) when (EsReintentable(ex))
            {
                if (intento >= _maxIntentos - 1)
                {
                    throw new DeadlockTransientException(
                        "El sistema está momentáneamente ocupado y no se pudo completar la operación. Intenta de nuevo.",
                        ex);
                }

                int delay = _baseDelayMs * (1 << intento) + Random.Shared.Next(0, 200);
                _logger.LogWarning(
                    "Conflicto transitorio de Firebird ({Tipo}). Reintento {Intento}/{Max} en {Delay}ms",
                    ex.Message, intento + 1, _maxIntentos, delay);

                await Task.Delay(delay);
            }
        }
    }

    /// <summary>
    /// Detecta deadlock / lock conflict / update conflict en Firebird,
    /// incluso dentro de excepciones anidadas.
    /// </summary>
    private static bool EsReintentable(Exception ex)
    {
        if (ex is DeadlockTransientException)
            return false;

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is not FbException fb)
                continue;

            // FirebirdSql marca los errores transitorios (deadlock, lock
            // conflict, update conflict, etc.) con IsTransient.
            if (fb.IsTransient)
                return true;

            // GDSCODE: 40001 deadlock, 40002 lock conflict, 40003 update conflict
            if (fb.ErrorCode is 40001 or 40002 or 40003)
                return true;

            if (fb.Errors != null)
            {
                foreach (var error in fb.Errors)
                {
                    if (error.Number is 40001 or 40002 or 40003)
                        return true;
                }
            }

            var state = fb.SQLSTATE;
            if (!string.IsNullOrEmpty(state) &&
                (state.StartsWith("40P0") || state.StartsWith("4000")))
                return true;
        }
        return false;
    }
}
