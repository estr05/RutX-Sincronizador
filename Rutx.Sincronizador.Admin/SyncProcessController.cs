using System.Diagnostics;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Controla el proceso del sincronizador (Rutx.Sincronizador.exe) como hijo:
/// lo arranca, lo detiene y captura su salida (stdout/stderr) para pintar los
/// logs en la ventana. No modifica nada del sincronizador: es puro control externo.
/// </summary>
public sealed class SyncProcessController : IDisposable
{
    private Process? _process;
    private readonly object _sync = new();

    public event EventHandler<string>? LogLine;
    public event EventHandler? EstadoCambio;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process != null && !_process.HasExited;
            }
        }
    }

    /// <summary>
    /// Intenta localizar el ejecutable del sincronizador:
    ///  1) junto al exe de la Admin (misma carpeta de publicacion),
    ///  2) en la carpeta de desarrollo (bin/Debug/net10.0 del proyecto).
    /// Devuelve null si no lo encuentra (el form pedira la ruta manualmente).
    /// </summary>
    public string? ResolverSyncExe()
    {
        var candidatos = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Rutx.Sincronizador.exe"),
            Path.Combine(Environment.CurrentDirectory, "Rutx.Sincronizador.exe"),
            Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                @"..\..\..\..\sincronizador_rutx\bin\Debug\net10.0")), "Rutx.Sincronizador.exe"),
            Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                @"..\..\..\..\sincronizador_rutx\bin\Release\net10.0")), "Rutx.Sincronizador.exe")
        };

        foreach (var c in candidatos)
        {
            if (System.IO.File.Exists(c))
                return c;
        }
        return null;
    }

    /// <summary>
    /// Arranca el sincronizador como proceso hijo con la salida redirigida.
    /// </summary>
    public void Iniciar(string rutaExe)
    {
        lock (_sync)
        {
            if (IsRunning) return;

            var psi = new ProcessStartInfo
            {
                FileName = rutaExe,
                WorkingDirectory = Path.GetDirectoryName(rutaExe) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (_, e) => Emitir(e.Data);
            _process.ErrorDataReceived += (_, e) => Emitir(e.Data);
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                Emitir("[PROCESO TERMINADO] El sincronizador se detuvo.");
                EstadoCambio?.Invoke(this, EventArgs.Empty);
            };

            if (_process.Start())
            {
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                Emitir("Sincronizador iniciado: " + rutaExe);
                EstadoCambio?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Emitir("ERROR: no se pudo iniciar el proceso.");
                _process = null;
            }
        }
    }

    /// <summary>
    /// Detiene el proceso (y su arbol). El lock anti-huerfanos del propio
    /// sincronizador limpia el residuo en %TEMP% al siguiente arranque.
    /// </summary>
    public void Detener()
    {
        Process? p;
        lock (_sync)
        {
            p = _process;
            if (p == null || p.HasExited)
            {
                _process = null;
                return;
            }
            _process = null;
        }

        try
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(3000);
            Emitir("Sincronizador detenido.");
        }
        catch (Exception ex)
        {
            Emitir("ERROR al detener: " + ex.Message);
        }
        finally
        {
            p.Dispose();
            EstadoCambio?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Emitir(string? linea)
    {
        if (string.IsNullOrWhiteSpace(linea)) return;
        LogLine?.Invoke(this, linea);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_process != null && !_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
                _process.Dispose();
            }
            _process = null;
        }
    }
}
