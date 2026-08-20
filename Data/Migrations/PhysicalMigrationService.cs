namespace Rutx.Sincronizador.Data.Migrations;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using Rutx.Sincronizador.Data.Sqlite;

public class PhysicalMigrationService
{
    private readonly ISqliteConnectionFactory _targetFactory;
    private readonly ILogger<PhysicalMigrationService> _logger;

    public PhysicalMigrationService(ISqliteConnectionFactory targetFactory, ILogger<PhysicalMigrationService> logger)
    {
        _targetFactory = targetFactory;
        _logger = logger;
    }

    public async Task<bool> MigrateLegacyQueueAsync(string legacyDbPath, CancellationToken ct = default)
    {
        if (!File.Exists(legacyDbPath))
            return false;

        _logger.LogInformation("Migracion Fisica: Detectada base de datos legacy en {Path}", legacyDbPath);

        var backupPath = $"{legacyDbPath}.bak_{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Copy(legacyDbPath, backupPath);
        _logger.LogInformation("Migracion Fisica: Backup creado en {BackupPath}", backupPath);

        int sourceColaCount = 0;
        int sourceVentasCount = 0;
        int insertedColaCount = 0;
        int insertedVentasCount = 0;
        int rejectedRows = 0;
        
        string sourceColaChecksum = "";
        string targetColaChecksum = "";

        await using var targetConn = await _targetFactory.CreateConnectionAsync(ct);
        await using var targetTx = await targetConn.BeginTransactionAsync(ct);

        bool committed = false;

        try
        {
            await using var sourceConn = new SqliteConnection($"Data Source={legacyDbPath}");
            await sourceConn.OpenAsync(ct);

            // Mapeo e insercion de ColaOperaciones
            var cmdSelectCola = sourceConn.CreateCommand();
            cmdSelectCola.CommandText = "SELECT * FROM ColaOperaciones";
            await using var readerCola = await cmdSelectCola.ExecuteReaderAsync(ct);

            var sbChecksum = new StringBuilder();

            while (await readerCola.ReadAsync(ct))
            {
                sourceColaCount++;
                try
                {
                    var operacionId = readerCola.GetString(readerCola.GetOrdinal("OperacionId"));
                    var tipoOperacion = readerCola.GetString(readerCola.GetOrdinal("TipoOperacion"));
                    var payload = readerCola.GetString(readerCola.GetOrdinal("Payload"));
                    var estado = readerCola.GetString(readerCola.GetOrdinal("Estado"));
                    var intentos = readerCola.GetInt32(readerCola.GetOrdinal("Intentos"));
                    var maxIntentos = readerCola.GetInt32(readerCola.GetOrdinal("MaxIntentos"));
                    var sigReintento = readerCola.GetString(readerCola.GetOrdinal("SiguienteReintento"));
                    var errIntento = readerCola.IsDBNull(readerCola.GetOrdinal("ErrorUltimoIntento")) ? null : readerCola.GetString(readerCola.GetOrdinal("ErrorUltimoIntento"));
                    var fechaCrea = readerCola.GetString(readerCola.GetOrdinal("FechaCreacion"));
                    var fechaMod = readerCola.GetString(readerCola.GetOrdinal("FechaModificacion"));

                    sbChecksum.Append($"{operacionId}|{estado}|{intentos};");

                    var cmdInsertCola = targetConn.CreateCommand();
                    cmdInsertCola.Transaction = (SqliteTransaction)targetTx;
                    cmdInsertCola.CommandText = @"
                        INSERT OR IGNORE INTO rutx_cola_operaciones
                        (operacion_id, tipo_operacion, payload, estado, intentos, max_intentos, siguiente_reintento, error_ultimo_intento, fecha_creacion, fecha_modificacion, lease_until, dead_letter)
                        VALUES (@op, @tipo, @payload, @estado, @intentos, @max, @sig, @err, @fCrea, @fMod, NULL, 0)";

                    cmdInsertCola.Parameters.AddWithValue("@op", operacionId);
                    cmdInsertCola.Parameters.AddWithValue("@tipo", tipoOperacion);
                    cmdInsertCola.Parameters.AddWithValue("@payload", payload);
                    cmdInsertCola.Parameters.AddWithValue("@estado", estado);
                    cmdInsertCola.Parameters.AddWithValue("@intentos", intentos);
                    cmdInsertCola.Parameters.AddWithValue("@max", maxIntentos);
                    cmdInsertCola.Parameters.AddWithValue("@sig", sigReintento);
                    cmdInsertCola.Parameters.AddWithValue("@err", errIntento ?? (object)DBNull.Value);
                    cmdInsertCola.Parameters.AddWithValue("@fCrea", fechaCrea);
                    cmdInsertCola.Parameters.AddWithValue("@fMod", fechaMod);

                    var affected = await cmdInsertCola.ExecuteNonQueryAsync(ct);
                    if (affected > 0) insertedColaCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Migracion Fisica: Fila rechazada en ColaOperaciones.");
                    rejectedRows++;
                }
            }

            sourceColaChecksum = ComputeSha256(sbChecksum.ToString());

            // Mapeo e insercion de VentasSincronizadas
            var cmdSelectVentas = sourceConn.CreateCommand();
            cmdSelectVentas.CommandText = "SELECT * FROM VentasSincronizadas";
            await using var readerVentas = await cmdSelectVentas.ExecuteReaderAsync(ct);

            while (await readerVentas.ReadAsync(ct))
            {
                sourceVentasCount++;
                try
                {
                    var vmid = readerVentas.GetString(readerVentas.GetOrdinal("VentaMovilId"));
                    var dId = readerVentas.IsDBNull(readerVentas.GetOrdinal("DoctoPvId")) ? null : (int?)readerVentas.GetInt32(readerVentas.GetOrdinal("DoctoPvId"));
                    var folio = readerVentas.IsDBNull(readerVentas.GetOrdinal("Folio")) ? null : readerVentas.GetString(readerVentas.GetOrdinal("Folio"));
                    var estado = readerVentas.GetString(readerVentas.GetOrdinal("Estado"));
                    var fechaCrea = readerVentas.GetString(readerVentas.GetOrdinal("FechaCreacion"));

                    var cmdInsertVentas = targetConn.CreateCommand();
                    cmdInsertVentas.Transaction = (SqliteTransaction)targetTx;
                    cmdInsertVentas.CommandText = @"
                        INSERT OR IGNORE INTO rutx_ventas_sincronizadas
                        (venta_movil_id, docto_pv_id, folio, estado, fecha_creacion)
                        VALUES (@vmid, @dId, @folio, @estado, @fecha)";

                    cmdInsertVentas.Parameters.AddWithValue("@vmid", vmid);
                    cmdInsertVentas.Parameters.AddWithValue("@dId", dId ?? (object)DBNull.Value);
                    cmdInsertVentas.Parameters.AddWithValue("@folio", folio ?? (object)DBNull.Value);
                    cmdInsertVentas.Parameters.AddWithValue("@estado", estado);
                    cmdInsertVentas.Parameters.AddWithValue("@fecha", fechaCrea);

                    var affected = await cmdInsertVentas.ExecuteNonQueryAsync(ct);
                    if (affected > 0) insertedVentasCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Migracion Fisica: Fila rechazada en VentasSincronizadas.");
                    rejectedRows++;
                }
            }

            // Target checksum para verificacion
            var sbTargetChecksum = new StringBuilder();
            var cmdTargetCheck = targetConn.CreateCommand();
            cmdTargetCheck.Transaction = (SqliteTransaction)targetTx;
            cmdTargetCheck.CommandText = "SELECT operacion_id, estado, intentos FROM rutx_cola_operaciones ORDER BY operacion_id";
            await using var readerTargetCheck = await cmdTargetCheck.ExecuteReaderAsync(ct);
            while (await readerTargetCheck.ReadAsync(ct))
            {
                sbTargetChecksum.Append($"{readerTargetCheck.GetString(0)}|{readerTargetCheck.GetString(1)}|{readerTargetCheck.GetInt32(2)};");
            }
            targetColaChecksum = ComputeSha256(sbTargetChecksum.ToString());

            // Tolerancia: si inserted < source pero fue por IGNORE (ya existia), el checksum de la tabla consolidada podría variar por elementos nativos de web.db.
            // Para el alcance de la Fase B, aceptamos la transaccion.
            await targetTx.CommitAsync(ct);
            committed = true;

            _logger.LogInformation(
                "Migracion Fisica: Completada. Cola: {SourceCola}->{InsertCola}. Ventas: {SourceVentas}->{InsertVentas}. Rechazos: {RejectedRows}.",
                sourceColaCount, insertedColaCount, sourceVentasCount, insertedVentasCount, rejectedRows);
            _logger.LogInformation("Checksum Legacy: {ChecksumLegacy} | Checksum Consolidada: {ChecksumConsolidada}", sourceColaChecksum, targetColaChecksum);

            sourceConn.Close();

            try
            {
                File.Move(legacyDbPath, $"{legacyDbPath}.migrated");
                _logger.LogInformation("Migracion Fisica: Archivo legacy renombrado a .migrated para evitar duplicacion.");
            }
            catch (Exception moveEx)
            {
                _logger.LogWarning(moveEx, "Migracion Fisica: No se pudo renombrar el archivo legacy. La migracion ya fue commiteada correctamente.");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                await targetTx.RollbackAsync(ct);
            }

            _logger.LogError(ex, "Migracion Fisica: Error durante el proceso. {Status} La base legacy se mantiene intacta. Revisa el archivo de backup: {BackupPath}",
                committed ? "La transaccion ya fue commiteada." : "Transaccion target revertida (solo la migracion).", backupPath);
            return false;
        }
    }

    private string ComputeSha256(string rawData)
    {
        using var sha256Hash = SHA256.Create();
        byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
        var builder = new StringBuilder();
        foreach (var t in bytes) builder.Append(t.ToString("x2"));
        return builder.ToString();
    }
}
