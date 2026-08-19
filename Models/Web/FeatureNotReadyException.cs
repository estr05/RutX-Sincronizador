namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Excepción lanzada cuando una funcionalidad del portal web
/// no está habilitada por configuración (feature flag en false).
/// </summary>
public sealed class FeatureNotReadyException : Exception
{
    public string Feature { get; }

    public FeatureNotReadyException(string feature)
        : base($"La funcionalidad '{feature}' no está disponible todavía.")
    {
        Feature = feature;
    }
}
