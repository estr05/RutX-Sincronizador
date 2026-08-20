using System.ServiceProcess;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Gestiona el servicio Windows "RutxSincronizador":
/// instalar, desinstalar, iniciar, detener y consultar estado.
/// Usa ServiceController para operaciones y sc.exe como fallback
/// para instalar/desinstalar (requiere elevacion).
/// </summary>
public static class ServiceHelper
{
    public const string ServiceName = "RutxSincronizador";
    public const string DisplayName = "RUTX Sincronizador";
    private const string Description = "API sincronizadora entre la app movil RUTX y Microsip (Firebird). Puerto 5047.";

    /// <summary>
    /// Estado actual del servicio (Running, Stopped, etc.) o null si no existe.
    /// </summary>
    public static ServiceControllerStatus? ObtenerEstado()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            // Esto lanza excepcion si el servicio no existe
            _ = sc.Status;
            return sc.Status;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True si el servicio esta registrado en Windows.
    /// </summary>
    public static bool Existe() => ObtenerEstado() != null;

    /// <summary>
    /// True si el servicio esta corriendo.
    /// </summary>
    public static bool EstaCorriendo()
    {
        var estado = ObtenerEstado();
        return estado == ServiceControllerStatus.Running || estado == ServiceControllerStatus.StartPending;
    }

    /// <summary>
    /// Instala el servicio Windows apuntando al exe del sincronizador.
    /// Requiere ejecucion como administrador. Devuelve (exito, mensaje).
    /// </summary>
    public static (bool ok, string mensaje) Instalar(string rutaExe, bool delayedAutoStart = true)
    {
        if (Existe())
            return (true, "El servicio ya esta instalado.");

        try
        {
            var startType = delayedAutoStart ? "delayed-auto" : "auto";
            var args = $"create \"{ServiceName}\" "
                     + $"binPath= \"{rutaExe}\" "
                     + $"DisplayName= \"{DisplayName}\" "
                     + $"start= {startType} "
                     + $"depend= \"\" ";

            var resultado = EjecutarSc(args);
            if (resultado.exitCode != 0)
                return (false, $"Error al instalar: {resultado.salida}");

            // Configurar descripcion
            EjecutarSc($"description \"{ServiceName}\" \"{Description}\"");

            // Politica de reinicio: reiniciar despues de 5s si falla, 3 intentos
            EjecutarSc($"failure \"{ServiceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");

            return (true, "Servicio instalado correctamente.");
        }
        catch (Exception ex)
        {
            return (false, $"Error inesperado: {ex.Message}");
        }
    }

    /// <summary>
    /// Desinstala el servicio Windows. Requiere administrador.
    /// </summary>
    public static (bool ok, string mensaje) Desinstalar()
    {
        if (!Existe())
            return (true, "El servicio no esta instalado.");

        try
        {
            // Detener primero si esta corriendo
            if (EstaCorriendo())
                Detener();

            var resultado = EjecutarSc($"delete \"{ServiceName}\"");
            if (resultado.exitCode != 0)
                return (false, $"Error al desinstalar: {resultado.salida}");

            return (true, "Servicio desinstalado correctamente.");
        }
        catch (Exception ex)
        {
            return (false, $"Error inesperado: {ex.Message}");
        }
    }

    public static (bool ok, string mensaje) Iniciar()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
                return (true, "El servicio ya esta corriendo.");

            sc.Start();
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                // Hard kill by executable name if start hangs to free port 5047
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Rutx.Sincronizador"))
                {
                    try { p.Kill(); } catch { }
                }
                return (false, "Error: timeout al iniciar el servicio. Se forzó el cierre del proceso huérfano.");
            }
            return (true, "Servicio iniciado.");
        }
        catch (Exception ex)
        {
            return (false, $"Error al iniciar: {ex.Message}");
        }
    }

    /// <summary>
    /// Detiene el servicio. Devuelve (exito, mensaje).
    /// </summary>
    public static (bool ok, string mensaje) Detener()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped)
                return (true, "El servicio ya esta detenido.");

            sc.Stop();
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                // Hard kill by executable name if it hangs
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Rutx.Sincronizador"))
                {
                    try { p.Kill(); } catch { }
                }
                return (false, "Error: timeout al detener el servicio. Se forzó el cierre.");
            }
            return (true, "Servicio detenido.");
        }
        catch (Exception ex)
        {
            return (false, $"Error al detener: {ex.Message}");
        }
    }

    private static (int exitCode, string salida) EjecutarSc(string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);

        var salida = stdout + stderr;
        return (proc.ExitCode, salida.Trim());
    }
}
