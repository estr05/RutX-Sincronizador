using Microsoft.Data.Sqlite;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Data;

public class ColaOfflineRepository : IColaOfflineRepository, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _conexionCompartida;
    private readonly bool _esCompartida;
    private bool _inicializado;

    public ColaOfflineRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public ColaOfflineRepository(SqliteConnection connection)
    {
        _conexionCompartida = connection;
        _connectionString = connection.ConnectionString;
        _esCompartida = true;
    }

    private async Task<SqliteConnection> AbrirConexionAsync()
    {
        SqliteConnection conn;
        if (_conexionCompartida != null)
        {
            conn = _conexionCompartida;
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();
        }
        else
        {
            conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
        }

        if (!_inicializado)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS ColaOperaciones (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    OperacionId         TEXT    NOT NULL UNIQUE,
                    TipoOperacion       TEXT    NOT NULL,
                    Payload             TEXT    NOT NULL,
                    Estado              TEXT    NOT NULL DEFAULT 'PENDIENTE',
                    Intentos            INTEGER NOT NULL DEFAULT 0,
                    MaxIntentos         INTEGER NOT NULL DEFAULT 5,
                    SiguienteReintento  TEXT    NOT NULL,
                    ErrorUltimoIntento  TEXT,
                    FechaCreacion       TEXT    NOT NULL,
                    FechaModificacion   TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ColaOperaciones_Estado ON ColaOperaciones(Estado);
                CREATE INDEX IF NOT EXISTS IX_ColaOperaciones_SiguienteReintento ON ColaOperaciones(SiguienteReintento);
                CREATE TABLE IF NOT EXISTS VentasSincronizadas (
                    VentaMovilId    TEXT PRIMARY KEY,
                    DoctoPvId       INTEGER,
                    Folio           TEXT,
                    Estado          TEXT NOT NULL DEFAULT 'PROCESANDO',
                    FechaCreacion   TEXT NOT NULL
                );";
            cmd.ExecuteNonQuery();
            _inicializado = true;
        }
        return conn;
    }

    private async Task<T> EjecutarAsync<T>(Func<SqliteConnection, Task<T>> accion)
    {
        var conn = await AbrirConexionAsync();
        try
        {
            return await accion(conn);
        }
        finally
        {
            if (!_esCompartida)
                await conn.DisposeAsync();
        }
    }

    private async Task EjecutarAsync(Func<SqliteConnection, Task> accion)
    {
        var conn = await AbrirConexionAsync();
        try
        {
            await accion(conn);
        }
        finally
        {
            if (!_esCompartida)
                await conn.DisposeAsync();
        }
    }

    public async Task<ColaOperacion> InsertarAsync(ColaOperacion operacion)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ColaOperaciones (OperacionId, TipoOperacion, Payload, Estado, Intentos, MaxIntentos, SiguienteReintento, ErrorUltimoIntento, FechaCreacion, FechaModificacion)
                VALUES (@OperacionId, @TipoOperacion, @Payload, @Estado, @Intentos, @MaxIntentos, @SiguienteReintento, @ErrorUltimoIntento, @FechaCreacion, @FechaModificacion);
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
            cmd.CommandText = "SELECT * FROM ColaOperaciones WHERE OperacionId = @OperacionId";
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
            // Operaciones PROCESANDO "colgadas": si el proceso murio a mitad del
            // procesamiento, el estado quedo en PROCESANDO para siempre y nunca
            // se reintentaba. Se liberan (vuelven a PENDIENTE) tras un umbral
            // de inactividad, mismo criterio que VentasSincronizadas (2 min).
            var liberar = conn.CreateCommand();
            liberar.CommandText = @"
                UPDATE ColaOperaciones
                SET Estado = 'PENDIENTE',
                    SiguienteReintento = @Ahora,
                    FechaModificacion = @Ahora
                WHERE Estado = 'PROCESANDO'
                  AND FechaModificacion <= @UmbralColgado";
            liberar.Parameters.AddWithValue("@Ahora", DateTime.UtcNow.ToString("O"));
            liberar.Parameters.AddWithValue("@UmbralColgado", DateTime.UtcNow.AddMinutes(-2).ToString("O"));
            await liberar.ExecuteNonQueryAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM ColaOperaciones
                WHERE Estado = 'PENDIENTE' AND SiguienteReintento <= @Ahora
                ORDER BY FechaCreacion ASC
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

    public async Task<List<ColaOperacion>> ObtenerPorEstadoAsync(EstadoOperacion estado, int limite = 100)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM ColaOperaciones
                WHERE Estado = @Estado
                ORDER BY FechaCreacion DESC
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
                UPDATE ColaOperaciones
                SET Estado = @Estado, Intentos = @Intentos, MaxIntentos = @MaxIntentos,
                    SiguienteReintento = @SiguienteReintento, ErrorUltimoIntento = @ErrorUltimoIntento,
                    FechaModificacion = @FechaModificacion
                WHERE OperacionId = @OperacionId";

            cmd.Parameters.AddWithValue("@OperacionId", operacion.OperacionId);
            cmd.Parameters.AddWithValue("@Estado", operacion.Estado.ToString());
            cmd.Parameters.AddWithValue("@Intentos", operacion.Intentos);
            cmd.Parameters.AddWithValue("@MaxIntentos", operacion.MaxIntentos);
            cmd.Parameters.AddWithValue("@SiguienteReintento", operacion.SiguienteReintento.ToString("O"));
            cmd.Parameters.AddWithValue("@ErrorUltimoIntento", operacion.ErrorUltimoIntento ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@FechaModificacion", operacion.FechaModificacion.ToString("O"));

            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task TocarHeartbeatAsync(string operacionId)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE ColaOperaciones
                SET FechaModificacion = @Ahora
                WHERE OperacionId = @OperacionId AND Estado = 'PROCESANDO'";
            cmd.Parameters.AddWithValue("@OperacionId", operacionId);
            cmd.Parameters.AddWithValue("@Ahora", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task EliminarAsync(string operacionId)
    {
        await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM ColaOperaciones WHERE OperacionId = @OperacionId";
            cmd.Parameters.AddWithValue("@OperacionId", operacionId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<int> ContarPorEstadoAsync(EstadoOperacion estado)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ColaOperaciones WHERE Estado = @Estado";
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
                SELECT * FROM ColaOperaciones
                WHERE TipoOperacion = @Tipo AND Payload LIKE @Idempotencia ESCAPE '\' AND Estado IN ('COMPLETADO', 'PENDIENTE', 'PROCESANDO')
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
                SELECT VentaMovilId, DoctoPvId, Folio, Estado, FechaCreacion
                FROM VentasSincronizadas WHERE VentaMovilId = @VentaMovilId";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            var venta = MapearVentaSincronizada(reader);

            // Marcador PROCESANDO "colgado" (proceso murio a mitad de la
            // venta): se libera para que el reintento pueda volver a intentar.
            if (venta.Estado == "PROCESANDO" &&
                DateTime.UtcNow - venta.FechaCreacion > TimeSpan.FromMinutes(2))
            {
                await LiberarVentaAsync(ventaMovilId);
                return null;
            }

            return venta;
        });
    }

    public async Task<bool> ReservarVentaAsync(string ventaMovilId)
    {
        return await EjecutarAsync(async conn =>
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO VentasSincronizadas (VentaMovilId, DoctoPvId, Folio, Estado, FechaCreacion)
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
                UPDATE VentasSincronizadas
                SET DoctoPvId = @DoctoPvId, Folio = @Folio, Estado = 'COMPLETADO'
                WHERE VentaMovilId = @VentaMovilId";
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
            cmd.CommandText = "DELETE FROM VentasSincronizadas WHERE VentaMovilId = @VentaMovilId";
            cmd.Parameters.AddWithValue("@VentaMovilId", ventaMovilId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private static VentaSincronizada MapearVentaSincronizada(SqliteDataReader reader)
    {
        return new VentaSincronizada
        {
            VentaMovilId = reader.GetString(reader.GetOrdinal("VentaMovilId")),
            DoctoPvId = reader.IsDBNull(reader.GetOrdinal("DoctoPvId"))
                ? null : reader.GetInt32(reader.GetOrdinal("DoctoPvId")),
            Folio = reader.IsDBNull(reader.GetOrdinal("Folio"))
                ? null : reader.GetString(reader.GetOrdinal("Folio")),
            Estado = reader.GetString(reader.GetOrdinal("Estado")),
            FechaCreacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("FechaCreacion")))
        };
    }

    private ColaOperacion MapearOperacion(SqliteDataReader reader)
    {
        return new ColaOperacion
        {
            Id = reader.GetInt32(reader.GetOrdinal("Id")),
            OperacionId = reader.GetString(reader.GetOrdinal("OperacionId")),
            TipoOperacion = Enum.Parse<TipoOperacion>(reader.GetString(reader.GetOrdinal("TipoOperacion"))),
            Payload = reader.GetString(reader.GetOrdinal("Payload")),
            Estado = Enum.Parse<EstadoOperacion>(reader.GetString(reader.GetOrdinal("Estado"))),
            Intentos = reader.GetInt32(reader.GetOrdinal("Intentos")),
            MaxIntentos = reader.GetInt32(reader.GetOrdinal("MaxIntentos")),
            SiguienteReintento = DateTime.Parse(reader.GetString(reader.GetOrdinal("SiguienteReintento"))),
            ErrorUltimoIntento = reader.IsDBNull(reader.GetOrdinal("ErrorUltimoIntento")) ? null : reader.GetString(reader.GetOrdinal("ErrorUltimoIntento")),
            FechaCreacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("FechaCreacion"))),
            FechaModificacion = DateTime.Parse(reader.GetString(reader.GetOrdinal("FechaModificacion")))
        };
    }

    public void Dispose()
    {
        _conexionCompartida?.Dispose();
        _conexionCompartida = null;
    }
}
