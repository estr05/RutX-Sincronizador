namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Catálogo de roles y permisos del portal (contrato v2 §8.1).
///
/// REGLA: el rol NO concede acceso por su nombre; la autorización efectiva
/// se evalúa contra los permisos resueltos (claims del JWT web). Este
/// catálogo es la única fuente de la asignación rol → permisos.
///
/// Roles habilitados para Coyatoc (MVP): administrador, supervisor, lector.
/// `contador` permanece RESERVADO y NO se emite en tokens ni se asigna
/// (contrato §8.1): no aparece en ninguna resolución.
/// </summary>
public static class WebRoleCatalog
{
    public const string Administrador = "administrador";
    public const string Supervisor = "supervisor";
    public const string Lector = "lector";
    public const string ContadorReservado = "contador";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> PermisosPorRol =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Administrador] = new[] { "reports.read", "customers.read", "inventory.read", "notifications.read", "notifications.send", "routes.monitor", "agendas.read" },
            [Supervisor] = new[] { "reports.read", "customers.read", "inventory.read", "notifications.read", "notifications.send", "routes.monitor" },
            [Lector] = new[] { "reports.read", "customers.read", "inventory.read", "notifications.read" },
        };

    /// <summary>Roles que el MVP puede emitir. `contador` jamás.</summary>
    public static IReadOnlyList<string> RolesHabilitados { get; } = new[] { Administrador, Supervisor, Lector };

    public static bool EsRolHabilitado(string rol) => PermisosPorRol.ContainsKey(rol);

    /// <summary>Permisos efectivos (unión sin duplicados) de un conjunto de roles.</summary>
    public static string[] ResolverPermisos(IEnumerable<string> roles)
    {
        var permisos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rol in roles)
        {
            if (PermisosPorRol.TryGetValue(rol, out var delRol))
                permisos.UnionWith(delRol);
        }
        return permisos.OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    public static bool TienePermiso(IEnumerable<string> roles, string permiso)
        => ResolverPermisos(roles).Contains(permiso, StringComparer.Ordinal);
}