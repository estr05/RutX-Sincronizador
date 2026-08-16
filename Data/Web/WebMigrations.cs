namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Catálogo de migraciones de la BD complementaria web.
///
/// Versiones:
///   v001 — esquema base del portal: usuarios de oficina (hash PBKDF2),
///          zonas y su mapeo a vendedores (WebZoneSellers), bandeja de
///          notificaciones y bitácora de auditoría.
///
/// Convenciones:
///   - Fechas: TEXT ISO 8601 (contrato v2 §4).
///   - Las tablas nuevas SIEMPRE llegan dentro de una migración versionada.
///   - No borrar ni renumerar migraciones publicadas; la nueva va al final.
/// </summary>
public static class WebMigrations
{
    public static readonly IReadOnlyList<WebMigration> All = new List<WebMigration>
    {
        new(1, "esquema-base-portal", EsquemaBasePortal),
    };

    private const string EsquemaBasePortal = @"
        CREATE TABLE IF NOT EXISTS web_users (
            id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            username             TEXT    NOT NULL UNIQUE,
            display_name         TEXT    NOT NULL DEFAULT '',
            password_hash        TEXT    NOT NULL,
            roles                TEXT    NOT NULL DEFAULT '[]',
            must_change_password INTEGER NOT NULL DEFAULT 0,
            password_changed_at  TEXT    NULL,
            is_active            INTEGER NOT NULL DEFAULT 1,
            created_at           TEXT    NOT NULL,
            updated_at           TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_web_users_username ON web_users(username);

        CREATE TABLE IF NOT EXISTS web_zones (
            zone_id INTEGER PRIMARY KEY,
            name    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS web_zone_sellers (
            zone_id   INTEGER NOT NULL REFERENCES web_zones(zone_id) ON DELETE CASCADE,
            seller_id INTEGER NOT NULL,
            PRIMARY KEY (zone_id, seller_id)
        );
        CREATE INDEX IF NOT EXISTS ix_web_zone_sellers_seller ON web_zone_sellers(seller_id);

        CREATE TABLE IF NOT EXISTS web_notifications (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            target_type     TEXT    NOT NULL,
            target_id       INTEGER NOT NULL,
            title           TEXT    NOT NULL,
            body            TEXT    NOT NULL DEFAULT '',
            priority        TEXT    NOT NULL DEFAULT 'normal',
            status          TEXT    NOT NULL DEFAULT 'active',
            sender_user_id  INTEGER NULL,
            sender_username TEXT    NULL,
            idempotency_key TEXT    NULL UNIQUE,
            trace_id        TEXT    NULL,
            created_at      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_web_notifications_target ON web_notifications(target_type, target_id);
        CREATE INDEX IF NOT EXISTS ix_web_notifications_status ON web_notifications(status);

        CREATE TABLE IF NOT EXISTS web_audit_log (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id    INTEGER NULL,
            username   TEXT    NULL,
            action     TEXT    NOT NULL,
            detail     TEXT    NULL,
            ip_address TEXT    NULL,
            trace_id   TEXT    NULL,
            created_at TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_web_audit_log_created ON web_audit_log(created_at);
        CREATE INDEX IF NOT EXISTS ix_web_audit_log_username ON web_audit_log(username);
    ";
}