namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Catálogo de migraciones de la BD complementaria web.
///
/// Versiones:
///   v001 — esquema base del portal: usuarios de oficina (hash PBKDF2),
///          zonas y su mapeo a vendedores (WebZoneSellers), bandeja de
///          notificaciones y bitácora de auditoría.
///   v002 — zonas autorizadas por usuario: columna web_users.zone_ids
///          (JSON). Vacío = usuario sin restricción de zona; el JWT web
///          materializa esos ids en el claim zone_ids (contrato v2 §5.1).
///   v003 — sesiones y ubicaciones de vendedor: rutx_seller_sessions
///          (jornada diaria del vendedor móvil) y rutx_seller_locations
///          (historial GPS de ubicaciones por sesión).
///   v004 — saga de no ventas: rutx_no_sale_operations (operación completa
///          con estados e idempotencia por venta_movil_id UNIQUE) y
///          rutx_media_files (metadata de archivos multimedia con
///          staging, promoción y hash SHA-256).
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
        new(1, "esquema-base-portal",            EsquemaBasePortal),
        new(2, "zonas-autorizadas-por-usuario",  ZonasAutorizadasPorUsuario),
        new(3, "sesiones-y-ubicaciones-vendedor", SesionesYUbicaciones),
        new(4, "saga-no-ventas-y-media",          SagaNoVentasYMedia),
        new(5, "consolidacion-cola-offline",      ConsolidacionColaOffline),
        new(6, "soporte-reintentos-saga",         SoporteReintentosSaga),
    };

    // ────────────────────────────────────────────────────────────────────────
    // v001 — Esquema base del portal
    // ────────────────────────────────────────────────────────────────────────
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
        CREATE INDEX IF NOT EXISTS ix_web_audit_log_created  ON web_audit_log(created_at);
        CREATE INDEX IF NOT EXISTS ix_web_audit_log_username ON web_audit_log(username);
    ";

    // ────────────────────────────────────────────────────────────────────────
    // v002 — Zonas autorizadas por usuario
    // ────────────────────────────────────────────────────────────────────────
    private const string ZonasAutorizadasPorUsuario = @"
        ALTER TABLE web_users ADD COLUMN zone_ids TEXT NOT NULL DEFAULT '[]';
        CREATE INDEX IF NOT EXISTS ix_web_users_zone_ids ON web_users(zone_ids);
    ";

    // ────────────────────────────────────────────────────────────────────────
    // v003 — Sesiones y ubicaciones del vendedor
    // ────────────────────────────────────────────────────────────────────────
    private const string SesionesYUbicaciones = @"
        -- Sesión diaria del vendedor en la app móvil.
        CREATE TABLE IF NOT EXISTS rutx_seller_sessions (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            seller_id   INTEGER NOT NULL,
            device_id   TEXT    NULL,
            app_version TEXT    NULL,
            status      TEXT    NOT NULL DEFAULT 'open',
            started_at  TEXT    NOT NULL,
            closed_at   TEXT    NULL,
            created_at  TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rutx_seller_sessions_seller  ON rutx_seller_sessions(seller_id);
        CREATE INDEX IF NOT EXISTS ix_rutx_seller_sessions_started ON rutx_seller_sessions(started_at);

        -- Historial GPS: una fila por actualización de ubicación.
        CREATE TABLE IF NOT EXISTS rutx_seller_locations (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id  INTEGER NOT NULL REFERENCES rutx_seller_sessions(id) ON DELETE CASCADE,
            seller_id   INTEGER NOT NULL,
            latitude    REAL    NOT NULL,
            longitude   REAL    NOT NULL,
            accuracy    REAL    NULL,
            recorded_at TEXT    NOT NULL,
            created_at  TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rutx_seller_locations_session ON rutx_seller_locations(session_id);
        CREATE INDEX IF NOT EXISTS ix_rutx_seller_locations_seller  ON rutx_seller_locations(seller_id);
        CREATE INDEX IF NOT EXISTS ix_rutx_seller_locations_time    ON rutx_seller_locations(recorded_at);
    ";

    // ────────────────────────────────────────────────────────────────────────
    // v004 — Saga de no ventas y archivos multimedia
    // ────────────────────────────────────────────────────────────────────────
    private const string SagaNoVentasYMedia = @"
        -- Operación de no-venta: una fila por intento de registro desde la app.
        -- venta_movil_id UNIQUE garantiza idempotencia bajo concurrencia (SQLite WAL).
        -- Estados: received → media_staged → firebird_committed → completed | failed
        CREATE TABLE IF NOT EXISTS rutx_no_sale_operations (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            venta_movil_id TEXT    NOT NULL UNIQUE,
            request_hash   TEXT    NOT NULL,
            vendedor_id    INTEGER NOT NULL,
            cliente_id     INTEGER NOT NULL,
            causa_id       INTEGER NOT NULL,
            fecha_hora     TEXT    NOT NULL,
            docto_pv_id    INTEGER NULL,
            folio          TEXT    NULL,
            foto_file_id   INTEGER NULL,
            status         TEXT    NOT NULL DEFAULT 'received',
            attempts       INTEGER NOT NULL DEFAULT 0,
            error_code     TEXT    NULL,
            error_message  TEXT    NULL,
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rutx_no_sale_venta_movil ON rutx_no_sale_operations(venta_movil_id);
        CREATE INDEX IF NOT EXISTS ix_rutx_no_sale_status      ON rutx_no_sale_operations(status);
        CREATE INDEX IF NOT EXISTS ix_rutx_no_sale_vendedor    ON rutx_no_sale_operations(vendedor_id);

        -- Archivos multimedia: foto de no-venta u otro adjunto.
        -- Ciclo: staging → completed | failed
        -- stored_name: nombre físico generado por el servidor (nunca del cliente).
        CREATE TABLE IF NOT EXISTS rutx_media_files (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            category      TEXT    NOT NULL DEFAULT 'no_sale_photo',
            operation_id  INTEGER NULL REFERENCES rutx_no_sale_operations(id) ON DELETE SET NULL,
            original_name TEXT    NULL,
            stored_name   TEXT    NOT NULL UNIQUE,
            relative_path TEXT    NOT NULL,
            mime_type     TEXT    NOT NULL,
            size_bytes    INTEGER NOT NULL,
            sha256        TEXT    NOT NULL,
            status        TEXT    NOT NULL DEFAULT 'staging',
            created_at    TEXT    NOT NULL,
            promoted_at   TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS ix_rutx_media_files_operation   ON rutx_media_files(operation_id);
        CREATE INDEX IF NOT EXISTS ix_rutx_media_files_stored_name ON rutx_media_files(stored_name);
        CREATE INDEX IF NOT EXISTS ix_rutx_media_files_status      ON rutx_media_files(status);
    ";

    // ────────────────────────────────────────────────────────────────────────
    // v005 — Consolidación de Cola Offline
    // ────────────────────────────────────────────────────────────────────────
    private const string ConsolidacionColaOffline = @"
        -- Tabla de cola unificada, con columnas adicionales para lease y dead_letter
        CREATE TABLE IF NOT EXISTS rutx_cola_operaciones (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            operacion_id        TEXT    NOT NULL UNIQUE,
            tipo_operacion      TEXT    NOT NULL,
            payload             TEXT    NOT NULL,
            estado              TEXT    NOT NULL DEFAULT 'PENDIENTE',
            intentos            INTEGER NOT NULL DEFAULT 0,
            max_intentos        INTEGER NOT NULL DEFAULT 5,
            siguiente_reintento TEXT    NOT NULL,
            error_ultimo_intento TEXT   NULL,
            fecha_creacion      TEXT    NOT NULL,
            fecha_modificacion  TEXT    NOT NULL,
            lease_until         TEXT    NULL,
            dead_letter         INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_estado ON rutx_cola_operaciones(estado);
        CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_lease  ON rutx_cola_operaciones(lease_until);
        CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_reintento ON rutx_cola_operaciones(siguiente_reintento);

        CREATE TABLE IF NOT EXISTS rutx_ventas_sincronizadas (
            venta_movil_id    TEXT PRIMARY KEY,
            docto_pv_id       INTEGER NULL,
            folio             TEXT NULL,
            estado            TEXT NOT NULL DEFAULT 'PROCESANDO',
            fecha_creacion    TEXT NOT NULL
        );
    ";
    // ────────────────────────────────────────────────────────────────────────
    // v006 — Soporte de reintentos para saga
    // ────────────────────────────────────────────────────────────────────────
    private const string SoporteReintentosSaga = @"
        ALTER TABLE rutx_no_sale_operations ADD COLUMN payload_json TEXT NULL;
        ALTER TABLE rutx_no_sale_operations ADD COLUMN session_json TEXT NULL;

        -- Migrar los estados a los nuevos nombres
        UPDATE rutx_no_sale_operations SET status = 'retryable_failed' WHERE status IN ('received', 'PENDING', 'media_staged', 'MEDIA_SYNCED', 'failed', 'FAILED');
        UPDATE rutx_no_sale_operations SET status = 'media_promotion_pending' WHERE status IN ('firebird_committed', 'DB_SYNCED');
        UPDATE rutx_no_sale_operations SET status = 'completed' WHERE status = 'COMPLETED';
    ";
}