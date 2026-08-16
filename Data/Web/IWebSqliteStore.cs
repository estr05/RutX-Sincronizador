namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Almacén de la BD complementaria web (SQLite). TODO el esquema se crea
/// mediante migraciones versionadas (WebSqliteMigrator); ninguna clase de
/// negocio ejecuta CREATE TABLE.
/// </summary>
public interface IWebSqliteStore
{
    /// <summary>Aplica migraciones pendientes (idempotente) y devuelve la versión vigente.</summary>
    Task<int> EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Versión vigente del esquema sin aplicar nada.</summary>
    Task<int> SchemaVersionAsync(CancellationToken cancellationToken = default);

    Task<WebUserRow?> FindUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<WebUserRow?> FindUserByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Crea un usuario con su contraseña ya hasheada (WebPasswordHasher).</summary>
    Task<WebUserRow> CreateUserAsync(
        string username,
        string displayName,
        string passwordHash,
        string rolesJson,
        bool mustChangePassword,
        CancellationToken cancellationToken = default);

    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea el administrador inicial SOLO desde configuración externa
    /// (WebAuth:AdminUsername/AdminPassword). Ver WebSqliteStore.EnsureAdminSeedAsync.
    /// </summary>
    Task<bool> EnsureAdminSeedAsync(IConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Vendedores (VENDEDOR_ID de Microsip) asignados a una zona.</summary>
    Task<List<int>> SellersForZoneAsync(int zoneId, CancellationToken cancellationToken = default);

    /// <summary>Zonas autorizadas para un vendedor (para validación cruzada).</summary>
    Task<List<int>> ZonesForSellerAsync(int sellerId, CancellationToken cancellationToken = default);

    Task<WebAuditEntry> LogAuditAsync(
        long? userId,
        string? username,
        string action,
        string? detail,
        string? ipAddress,
        string? traceId,
        CancellationToken cancellationToken = default);
}