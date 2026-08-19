namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Migración versionada de la BD complementaria web (SQLite).
///
/// REGLA (Sprint 4 · Bloque 0): el esquema web se evoluciona EXCLUSIVAMENTE
/// mediante migraciones versionadas. Cada migración es atómica (transacción),
/// registra su versión en `schema_version` y, antes de aplicar la primera
/// migración pendiente, se genera un respaldo consistente del archivo .db
/// (VACUUM INTO) en `Data/backups/`.
///
/// Nunca se agrega una tabla o columna directa dentro de un servicio;
/// si cambia el contrato, cambia la migración y se versiona.
/// </summary>
public sealed record WebMigration(int Version, string Name, string Sql);