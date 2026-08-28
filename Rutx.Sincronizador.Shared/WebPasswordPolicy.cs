namespace Rutx.Sincronizador.Shared;

/// <summary>
/// Politica central de contrasena del panel web administrativo (/admin).
/// Unica fuente de verdad para que el wizard de instalacion, el helper de
/// instalacion y ProductionConfigurationValidator apliquen exactamente las
/// mismas reglas sin duplicacion.
///
/// Reglas vigentes:
///   - Obligatoria (no vacia).
///   - Longitud minima coherente = 8 caracteres.
///   - No puede ser "admin" (usuario por defecto del panel).
///   - No puede coincidir (case-insensitive) con el usuario del panel
///     (WebAuth:AdminUsername), nunca con el usuario de Firebird.
///   - No puede contener el placeholder CHANGE_ME.
/// </summary>
public static class WebPasswordPolicy
{
    public const int MinLongitud = 8;
    public const string UsuarioPanelDefault = "admin";
    private const string Placeholder = "CHANGE_ME";

    /// <summary>
    /// Valida una contrasena del panel web. Devuelve true si es valida y
    /// setea el mensaje de error en <paramref name="error"/> cuando no lo es.
    /// </summary>
    public static bool EsValida(string? password, string? panelUser, out string error)
    {
        error = string.Empty;

        var pass = password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(pass))
        {
            error = "La contrasena del panel web es obligatoria.";
            return false;
        }

        if (pass.Trim().Length < MinLongitud)
        {
            error = $"La contrasena del panel web debe tener al menos {MinLongitud} caracteres.";
            return false;
        }

        var usuario = string.IsNullOrWhiteSpace(panelUser) ? UsuarioPanelDefault : panelUser.Trim();

        if (pass.Equals(usuario, StringComparison.OrdinalIgnoreCase))
        {
            error = "La contrasena del panel web no puede ser identica al usuario.";
            return false;
        }

        if (pass.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            error = "La contrasena del panel web no puede ser 'admin'.";
            return false;
        }

        if (pass.Contains(Placeholder, StringComparison.OrdinalIgnoreCase))
        {
            error = "La contrasena del panel web no puede contener el placeholder CHANGE_ME.";
            return false;
        }

        return true;
    }
}
