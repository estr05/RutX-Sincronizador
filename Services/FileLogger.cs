using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// FileLoggerProvider: escribe logs a archivos rotados por dia.
/// Se usa como logger auxiliar cuando corre como servicio Windows
/// (no hay consola disponible).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDir;
    private readonly object _lock = new();

    public FileLoggerProvider(string logsDir) => _logsDir = logsDir;

    public ILogger CreateLogger(string categoryName) => new FileLogger(_logsDir, _lock);

    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _logsDir;
    private readonly object _lock;

    public FileLogger(string logsDir, object @lock)
    {
        _logsDir = logsDir;
        _lock = @lock;
    }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var mensagem = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(mensagem)) return;

        var nivel = logLevel switch
        {
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "FTL",
            _ => "INF"
        };

        var linha = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{nivel}] {mensagem}";
        if (exception != null)
            linha += $"\n  Exception: {exception}";

        var arquivo = Path.Combine(_logsDir, $"sincronizador_{DateTime.Now:yyyy-MM-dd}.log");

        lock (_lock)
        {
            try { File.AppendAllText(arquivo, linha + Environment.NewLine); }
            catch { /* no critico */ }
        }
    }
}
