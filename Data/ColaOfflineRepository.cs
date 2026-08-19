namespace Rutx.Sincronizador.Data;

using Microsoft.Data.Sqlite;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Data.Sqlite;

public class ColaOfflineRepository : IColaOfflineRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public ColaOfflineRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    private async Task<T> EjecutarAsync<T>(Func<SqliteConnection, Task<T>> accion)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync();
        return await accion(conn);
    }

    private async Task EjecutarAsync(Func<SqliteConnection, Task> accion)
    {
        await using var conn = await _connectionFactory.CreateConnectionAsync();
        await accion(conn);
    }

    public async Task<ColaOperacion> InsertarAsync(ColaOperacion operacion)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO rutx_cola_operaciones (operacion_id, tipo_operacion, payload, estado, intentos, max_intentos, siguiente_reintento, error_ultimo_intento, fecha_creacion, fecha_modificacion, lease_until, dead_letter)
                VALUES (@OperacionId, @TipoOperacion, @Payload, @Estado, @Intentos, @MaxIntentos, @SiguienteReintento, @ErrorUltimoIntento, @FechaCreacion, @FechaModificacion, @LeaseUntil, @DeadLetter);
                SELECT last_insert_rowid();";

            cmd.Parameters.AddWithValue("@OperacionId", operacion.OperacionId);
            cmd.Parameters.AddWithValue("@TipoOperacion", operacion.TipoOperacion.ToString());
            cmd.Parameters.AddWithValue("@Payload", operacion.Payload);
            cmd.Parameters.AddWithValue("@Estado", operacion.Estado.ToString());
            cmd.Parameters.AddWithValue("@Intentos", operacion.Intentos);
            cmd.Parameters.AddWithValue("@MaxIntentos", operacion.MaxIntentos);
            cmd.Parameters.AddWithValue("@SiguienteReintento", operacion.SiguienteReintento.ToString("O"));
            cmd.Parameters.AddWithValue("@ErrorUltimoIntento", operacion.ErrorUltimoIntento ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FechaCreacion", operacion.FechaCreacion.ToString("O"));
            cmd.Parameters.AddWithValue("@FechaModificacion", operacion.FechaModificacion.ToString("O"));
            cmd.Parameters.AddWithValue("@LeaseUntil", operacion.LeaseUntil?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DeadLetter", operacion.DeadLetter ? 1 : 0);

            var id = await cmd.ExecuteScalarAsync();
            operacion.Id = Convert.ToInt32(id);
            return operacion;
        });
    }

    public async Task<ColaOperacion?> ObtenerPorIdAsync(string operacionId)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM rutx_cola_operaciones WHERE operacion_id = @OperacionId";
            cmd.Parameters.AddWithValue("@OperacionId", operacionId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return MapearOperacion(reader);

            return null;
        });
    }

    public async Task<List<ColaOperacion>> ObtenerPendientesAsync(int limite = 10)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            // Implementacion de captura atómica guiada por Fase D del plan: 
            // no lo hacemos en el GET, lo haremos en un metodo especial de reclamacion (Lease) o en el worker directamente.
            // Para mantener compatibilidad con el worker anterior, retornamos los pendientes:
            cmd.CommandText = @"
                SELECT * FROM rutx_cola_operaciones
                WHERE estado = 'PENDIENTE' AND siguiente_reintento <= @Ahora AND dead_letter = 0
                ORDER BY fecha_creacion ASC
                LIMIT @Limite";
            cmd.Parameters.AddWithValue("@Ahora", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Limite", limite);

            var operaciones = new List<ColaOperacion>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                operaciones.Add(MapearOperacion(reader));

            return operaciones;
        });
    }

    public async Task<List<ColaOperacion>> ReclamarPendientesAsync(int limite = 10, TimeSpan? leaseDuration = null)
    {
        return await EjecutarAsync(async conn =>
        {
            var ahora = DateTime.UtcNow;
            var leaseUntil = ahora.Add(leaseDuration ?? TimeSpan.FromMinutes(2));
            var ahoraStr = ahora.ToString("O");
            var leaseUntilStr = leaseUntil.ToString("O");

            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            var cmdSelect = conn.CreateCommand();
            cmdSelect.Transaction = tx;
            // Selecciona operaciones pendientes o aquellas cuyo lease expiró
            cmdSelect.CommandText = @"
                SELECT * FROM rutx_cola_operaciones
                WHERE estado = 'PENDIENTE' 
                  AND dead_letter = 0
                  AND siguiente_reintento <= @Ahora
                  AND (lease_until IS NULL OR lease_until <= @Ahora)
                ORDER BY fecha_creacion ASC
                LIMIT @Limite";
            cmdSelect.Parameters.AddWithValue("@Ahora", ahoraStr);
            cmdSelect.Parameters.AddWithValue("@Limite", limite);

            var operaciones = new List<ColaOperacion>();
            using (var reader = await cmdSelect.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    operaciones.Add(MapearOperacion(reader));
                }
            }

            if (operaciones.Count > 0)
            {
                var cmdUpdate = conn.CreateCommand();
                cmdUpdate.Transaction = tx;
                var ids = string.Join(",", operaciones.Select(o => o.Id));
                cmdUpdate.CommandText = $@"
                    UPDATE rutx_cola_operaciones
                    SET lease_until = @LeaseUntil, fecha_modificacion = @Ahora
                    WHERE id IN ({ids})";
                cmdUpdate.Parameters.AddWithValue("@LeaseUntil", leaseUntilStr);
                cmdUpdate.Parameters.AddWithValue("@Ahora", ahoraStr);
                await cmdUpdate.ExecuteNonQueryAsync();

                // Actualizar los objetos en memoria
                foreach (var op in operaciones)
                {
                    op.LeaseUntil = leaseUntil;
                    op.FechaModificacion = ahora;
                }
            }

            await tx.CommitAsync();

            return operaciones;
        });
    }

    public async Task<List<ColaOperacion>> ObtenerPorEstadoAsync(EstadoOperacion estado, int limite = 100)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM rutx_cola_operaciones
                WHERE estado = @Estado AND dead_letter = 0
                ORDER BY fecha_creacion DESC
                LIMIT @Limite";
            cmd.Parameters.AddWithValue("@Estado", estado.ToString());
            cmd.Parameters.AddWithValue("@Limite", limite);

            var operaciones = new List<ColaOperacion>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                operaciones.Add(MapearOperacion(reader));

            return operaciones;
        });
    }

    public async Task ActualizarAsync(ColaOperacion operacion)
    {
        operacion.FechaModificacion = DateTime.UtcNow;
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE rutx_cola_operaciones
                SET estado = @Estado, intentos = @Intentos, max_intentos = @MaxIntentos,
                    siguiente_reintento = @SiguienteReintento, error_ultimo_intento = @ErrorUltimoIntento,
                    fecha_modificacion = @FechaModificacion, lease_until = @LeaseUntil, dead_letter = @DeadLetter
                WHERE operacion_id = @OperacionId";

            cmd.Parameters.AddWithValue("@OperacionId", operacion.OperacionId);
            cmd.Parameters.AddWithValue("@Estado", operacion.Estado.ToString());
            cmd.Parameters.AddWithValue("@Intentos", operacion.Intentos);
            cmd.Parameters.AddWithValue("@MaxIntentos", operacion.MaxIntentos);
            cmd.Parameters.AddWithValue("@SiguienteReintento", operacion.SiguienteReintento.ToString("O"));
            cmd.Parameters.AddWithValue("@ErrorUltimoIntento", operacion.ErrorUltimoIntento ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FechaModificacion", operacion.FechaModificacion.ToString("O"));
            cmd.Parameters.AddWithValue("@LeaseUntil", operacion.LeaseUntil?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DeadLetter", operacion.DeadLetter ? 1 : 0);

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task TocarHeartbeatAsync(string operacionId)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE rutx_cola_operaciones
                SET fecha_modificacion = @Ahora, lease_until = @Lease
                WHERE operacion_id = @OperacionId";
            cmd.Parameters.AddWithValue("@OperacionId", operacionId);
            var ahora = DateTime.UtcNow;
            cmd.Parameters.AddWithValue("@Ahora", ahora.ToString("O"));
            cmd.Parameters.AddWithValue("@Lease", ahora.AddMinutes(2).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task EliminarAsync(string operacionId)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rutx_cola_operaciones WHERE operacion_id = @OperacionId";
            cmd.Parameters.AddWithValue("@OperacionId", operacionId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<int> ContarPorEstadoAsync(EstadoOperacion estado)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM rutx_cola_operaciones WHERE estado = @Estado";
            cmd.Parameters.AddWithValue("@Estado", estado.ToString());
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    public async Task<ColaOperacion?> ObtenerPorIdempotenciaAsync(TipoOperacion tipo, string idempotencia)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            var escaped = idempotencia.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.CommandText = @"
                SELECT * FROM rutx_cola_operaciones
                WHERE tipo_operacion = @Tipo AND payload LIKE @Idempotencia ESCAPE '\' AND estado IN ('COMPLETADO', 'PENDIENTE', 'PROCESANDO')
                LIMIT 1";
            cmd.Parameters.AddWithValue("@Tipo", tipo.ToString());
            cmd.Parameters.AddWithValue("@Idempotencia", $"%{escaped}%");

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return MapearOperacion(reader);

            return null;
        });
    }

    public async Task<VentaSincronizada?> ObtenerVentaSincronizadaAsync(string ventaMovilId)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT venta_movil_id, docto_pv_id, folio, estado, fecha_creacion
                FROM rutx_ventas_sincronizadas WHERE venta_movil_id = @VentaMovilId";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var venta = MapearVentaSincronizada(reader);

            if (venta.Estado == "PROCESANDO")
            {
                var ahora = DateTime.UtcNow;
                var ultimaActividad = await ObtenerFechaModificacionVentaAsync(conn, ventaMovilId) ?? venta.FechaCreacion;
                if (ahora - ultimaActividad > TimeSpan.FromMinutes(2))
                {
                    await LiberarVentaAsync(ventaMovilId);
                    return null;
                }
            }

            return venta;
        });
    }

    private static async Task<DateTime?> ObtenerFechaModificacionVentaAsync(SqliteConnection conn, string ventaMovilId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT fecha_creacion FROM rutx_ventas_sincronizadas WHERE venta_movil_id = @VentaMovilId";
        cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);
        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? DateTime.Parse(s) : null;
    }

    public async Task<bool> ReservarVentaAsync(string ventaMovilId)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO rutx_ventas_sincronizadas (venta_movil_id, docto_pv_id, folio, estado, fecha_creacion)
                VALUES (@VentaMovilId, NULL, NULL, 'PROCESANDO', @FechaCreacion)";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);
            cmd.Parameters.AddWithValue("@FechaCreacion", DateTime.UtcNow.ToString("O"));

            return await cmd.ExecuteNonQueryAsync() > 0;
        });
    }

    public async Task CompletarVentaAsync(string ventaMovilId, int doctoPvId, string folio)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE rutx_ventas_sincronizadas
                SET docto_pv_id = @DoctoPvId, folio = @Folio, estado = 'COMPLETADO'
                WHERE venta_movil_id = @VentaMovilId";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);
            cmd.Parameters.AddWithValue("@DoctoPvId", doctoPvId);
            cmd.Parameters.AddWithValue("@Folio", folio);

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task LiberarVentaAsync(string ventaMovilId)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rutx_ventas_sincronizadas WHERE venta_movil_id = @VentaMovilId";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private static VentaSincronizada MapearVentaSincronizada(SqliteDataReader reader)
    {
        return new VentaSincronizada
        {
            VentaMovilId = reader.GetString(reader.GetOrdinal("venta_movil_id")),
            DoctoPvId = reader.IsDBNull(reader.GetOrdinal("docto_pv_id"))
                ? null : reader.GetInt32(reader.GetOrdinal("docto_pv_id")),
            Folio = reader.IsDBNull(reader.GetOrdinal("folio"))
                ? null : reader.GetString(reader.GetOrdinal("folio")),
            Estado = reader.GetString(reader.GetOrdinal("estado")),
            FechaCreacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("fecha_creacion")))
        };
    }

    private ColaOperacion MapearOperacion(SqliteDataReader reader)
    {
        return new ColaOperacion
        {
            Id = reader.GetInt32(reader.GetOrdinal("id")),
            OperacionId = reader.GetString(reader.GetOrdinal("operacion_id")),
            TipoOperacion = Enum.Parse<TipoOperacion>(reader.GetString(reader.GetOrdinal("tipo_operacion"))),
            Payload = reader.GetString(reader.GetOrdinal("payload")),
            Estado = Enum.Parse<EstadoOperacion>(reader.GetString(reader.GetOrdinal("estado"))),
            Intentos = reader.GetInt32(reader.GetOrdinal("intentos")),
            MaxIntentos = reader.GetInt32(reader.GetOrdinal("max_intentos")),
            SiguienteReintento = DateTime.Parse(reader.GetString(reader.GetOrdinal("siguiente_reintento"))),
            ErrorUltimoIntento = reader.IsDBNull(reader.GetOrdinal("error_ultimo_intento")) ? null : reader.GetString(reader.GetOrdinal("error_ultimo_intento")),
            FechaCreacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("fecha_creacion"))),
            FechaModificacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("fecha_modificacion"))),
            LeaseUntil = reader.IsDBNull(reader.GetOrdinal("lease_until")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("lease_until"))),
            DeadLetter = reader.GetInt32(reader.GetOrdinal("dead_letter")) != 0
        };
    }
}
