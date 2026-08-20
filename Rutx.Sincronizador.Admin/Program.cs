namespace Rutx.Sincronizador.Admin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => MostrarCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => MostrarCrash(e.ExceptionObject as Exception);

        // Modo elevado: el launcher se relanzo con permisos de administrador
        // (UAC) para operar el servicio de Windows. Se ejecuta la operacion,
        // se muestra el resultado y se sale sin abrir la ventana.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--instalar-servicio":
                    EjecutarOperacionServicio("instalar", args);
                    return;
                case "--desinstalar-servicio":
                    EjecutarOperacionServicio("desinstalar", args);
                    return;
                case "--iniciar-servicio":
                    EjecutarOperacionServicio("iniciar", args);
                    return;
                case "--detener-servicio":
                    EjecutarOperacionServicio("detener", args);
                    return;
            }
        }

        Application.Run(new AdminForm());
    }

    /// <summary>
    /// Ejecuta la operacion sobre el servicio en el proceso elevado y muestra
    /// el resultado al usuario. Al terminar, el launcher original refresca
    /// su estado (boton, indicadores).
    /// </summary>
    private static void EjecutarOperacionServicio(string operacion, string[] args)
    {
        string? rutaExe = args.Length > 1 ? args[1] : null;
        if (string.IsNullOrWhiteSpace(rutaExe))
        {
            var instalacion = InstalacionHelper.BuscarInstalacion();
            rutaExe = instalacion?.ExeSync;
        }

        (bool ok, string mensaje) resultado;
        if (operacion == "instalar")
        {
            if (string.IsNullOrWhiteSpace(rutaExe))
            {
                MessageBox.Show(
                    "No se pudo instalar el servicio.\n\nNo se localizo Rutx.Sincronizador.exe.",
                    "RUTX · Servicio Windows", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            resultado = ServiceHelper.Instalar(rutaExe);
        }
        else if (operacion == "desinstalar")
            resultado = ServiceHelper.Desinstalar();
        else if (operacion == "iniciar")
            resultado = ServiceHelper.Iniciar();
        else
            resultado = ServiceHelper.Detener();

        string tituloOk = operacion switch
        {
            "instalar" => "El servicio se instalo correctamente.",
            "desinstalar" => "El servicio se desinstalo correctamente.",
            "iniciar" => "El servicio se inicio correctamente.",
            _ => "El servicio se detuvo correctamente."
        };
        string tituloError = operacion switch
        {
            "instalar" => "No se pudo instalar el servicio.",
            "desinstalar" => "No se pudo desinstalar el servicio.",
            "iniciar" => "No se pudo iniciar el servicio.",
            _ => "No se pudo detener el servicio."
        };

        MessageBox.Show(
            resultado.ok ? tituloOk : $"{tituloError}\n\n{resultado.mensaje}",
            "RUTX · Servicio Windows",
            MessageBoxButtons.OK,
            resultado.ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    private static void MostrarCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var msg = $"El launcher sufrió un error inesperado:\n\n{ex.Message}";
            using var f = new Form { TopMost = true };
            MessageBox.Show(f, msg, "RUTX · Error Crítico", MessageBoxButtons.OK, MessageBoxIcon.Error);
            System.IO.File.AppendAllText("launcher_crash.log", $"[{DateTime.Now}] {ex}\n\n");
        }
        catch { /* Fallback fail silently if we can't even show a messagebox */ }
        finally
        {
            Application.Exit();
        }
    }
}
