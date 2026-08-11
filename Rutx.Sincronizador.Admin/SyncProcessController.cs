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

    // Persistencia de la ruta elegida manualmente (evita pedirla en cada arranque).
    private static readonly string RutaConfig = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Rutx", "sync_path.txt");

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
    /// Localiza el ejecutable del sincronizador en este orden:
    ///  1) ruta persistida (si el usuario la eligió manualmente),
    ///  2) junto al exe de la Admin (carpeta de publicación),
    ///  3) bin del proyecto del sync (Debug/Release) usando rutas relativas
    ///     desde la ubicación del launcher,
    ///  4) bin del proyecto del sync relativo al directorio de trabajo.
    /// Devuelve null si no lo encuentra (el form pedirá la ruta manualmente).
    /// </summary>
    public string? ResolverSyncExe()
    {
        // 1) Ruta persistida por el usuario
        var persistida = LeerRutaPersistida();
        if (!string.IsNullOrWhiteSpace(persistida) && System.IO.File.Exists(persistida))
            return persistida;

        // 2) Junto al exe de la Admin (producción: misma carpeta de publicación)
        var juntoAdmin = Path.Combine(AppContext.BaseDirectory, "Rutx.Sincronizador.exe");
        if (System.IO.File.Exists(juntoAdmin))
            return juntoAdmin;

        var dirActual = Environment.CurrentDirectory;
        var baseDir = AppContext.BaseDirectory;

        // 3) Relativas al BaseDirectory del launcher.
        //    En dev el launcher vive en:
        //      <repo>/Rutx.Sincronizador.Admin/bin/Debug/net10.0/
        //    y subir 4 niveles llega a la raíz del repo <repo>/:
        //      net10.0 -> Debug -> bin -> Rutx.Sincronizador.Admin -> <repo>
        var candidatosRelativos = new[]
        {
            Path.Combine(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\bin\Debug\net10.0")), "Rutx.Sincronizador.exe"),
            Path.Combine(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\bin\Release\net10.0")), "Rutx.Sincronizador.exe")
        };

        // 4) Relativas al directorio de trabajo (si se lanzó desde la raíz del repo)
        var candidatosDirTrabajo = new[]
        {
            Path.Combine(dirActual, "bin", "Debug", "net10.0", "Rutx.Sincronizador.exe"),
            Path.Combine(dirActual, "bin", "Release", "net10.0", "Rutx.Sincronizador.exe"),
            Path.Combine(dirActual, "Rutx.Sincronizador", "bin", "Debug", "net10.0", "Rutx.Sincronizador.exe")
        };

        foreach (var c in candidatosRelativos.Concat(candidatosDirTrabajo))
        {
            if (System.IO.File.Exists(c))
                return c;
        }
        return null;
    }

    /// <summary>
    /// Persiste la ruta del sync elegida manualmente para el próximo arranque.
    /// </summary>
    public void GuardarRuta(string rutaExe)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RutaConfig)!);
            System.IO.File.WriteAllText(RutaConfig, rutaExe);
        }
        catch
        {
            // Si no se puede guardar, no es crítico: se vuelve a pedir la próxima vez.
        }
    }

    private static string? LeerRutaPersistida()
    {
        try
        {
            return System.IO.File.Exists(RutaConfig)
                ? System.IO.File.ReadAllText(RutaConfig).Trim()
                : null;
        }
        catch
        {
            return null;
        }
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
    /// Detiene el proceso (y su árbol). El lock anti-huérfanos del propio
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
