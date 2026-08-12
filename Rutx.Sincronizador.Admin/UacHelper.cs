using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Utilidades de elevacion UAC: detectar si el proceso corre como
/// administrador y relanzar la aplicacion pidiendo permisos elevados
/// (el aviso clasico "¿Quieres permitir que esta aplicacion haga cambios
/// en este dispositivo?").
/// </summary>
public static class UacHelper
{
    /// <summary>
    /// True si el proceso actual se ejecuta con privilegios de administrador.
    /// </summary>
    public static bool EsAdministrador()
    {
        using var identidad = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identidad).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Relanza esta aplicacion elevada (muestra el prompt de UAC) con los
    /// argumentos indicados. Devuelve el proceso iniciado o null si el
    /// usuario cancelo el prompt ("No" en el aviso de UAC).
    /// </summary>
    public static Process? RelanzarComoAdministrador(string argumentos)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = argumentos,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            return Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // El usuario cancelo el aviso de UAC (error 1223) o la elevacion
            // no fue posible (p. ej. error 740: cuenta sin derechos / UAC
            // deshabilitado). En ambos casos no hay proceso que esperar.
            return null;
        }
    }
}
