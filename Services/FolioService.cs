using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Servicio de folios sobre FOLIOS_CAJAS que elimina la condición de carrera
/// SELECT-then-UPDATE (causa del isc_deadlock).
///
/// RESERVA ATÓMICA:
///   UPDATE FOLIOS_CAJAS SET CONSECUTIVO = CONSECUTIVO + 1
///   WHERE CAJA_ID = @CajaId AND TIPO_DOCTO = @Tipo
///   RETURNING CONSECUTIVO, SERIE;
///
/// En el modelo de Microsip, CONSECUTIVO es el PRÓXIMO folio a usar
/// (evidencia: caja LC, 10 ventas 0001..0010 y CONSECUTIVO=11). Por lo
/// tanto el folio a emitir AHORA = (CONSECUTIVO devuelto - 1):
///   - fila nueva CONSECUTIVO=1  ->  UPDATE devuelve 2 -> folio 0000001
///   - tras 10 ventas CONS=11     ->  UPDATE devuelve 12 -> folio 0000011
///
/// AUTO-CREACIÓN DE BLOQUE (espejo de Microsip):
///   Si la caja no tiene fila para el tipo, se crean las 8 filas que crea
///   Microsip al alta de una caja: T y V comparten la primera serie; luego
///   O, D, R, I, P, S usan las series consecutivas siguientes. El bloque
///   siguiente arranca en MAX(SERIE) - 5 (evidencia en la BD: bloques
///   consecutivos AA.., LB.., LC.., ..., LI..). Configurable con
///   MicrosipSettings:AutoCrearFolios (default true).
/// </summary>
public interface IFolioService
{
    /// <summary>
    /// Reserva atómicamente el siguiente folio para la caja/tipo dentro de
    /// la transacción que recibe. Devuelve el folio formateado (ej: LE0000081).
    /// Crea el bloque espejo automáticamente si la caja no tiene folios.
    /// </summary>
    Task<string> ReservarFolioAsync(FbConnection connection, FbTransaction transaction, int cajaId, string tipoDocto);

    /// <summary>
    /// Reparación única: alinea CONSECUTIVO de las filas V/P con
    /// MAX(folio emitido) + 1. Genera un respaldo en disco antes de cambiar.
    /// Devuelve la cantidad de filas corregidas.
    /// </summary>
    Task<int> RepararContadoresAsync();
}

public class FolioService : IFolioService
{
    private static readonly (string Tipo, int DesplazamientoSerie)[] _bloque =
    {
        ("T", 0), ("V", 0), ("O", 1), ("D", 2), ("R", 3), ("I", 4), ("P", 5), ("S", 6)
    };

    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly ILogger<FolioService> _logger;
    private readonly bool _autoCrearFolios;

    public FolioService(IConfiguration configuration, ILogger<FolioService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");
        _autoCrearFolios = configuration.GetValue<bool>("MicrosipSettings:AutoCrearFolios", true);
    }

    public async Task<string> ReservarFolioAsync(
        FbConnection connection, FbTransaction transaction, int cajaId, string tipoDocto)
    {
        // Intentos limitados: cubre la carrera de dos procesos creando el
        // bloque a la vez (uno gana el INSERT, el otro reintenta la UPDATE)
        // y el contador desfasado (folio reservado que ya existe en DOCTOS_PV).
        for (int intento = 0; intento < 3; intento++)
        {
            var reservado = await connection.QueryFirstOrDefaultAsync(@"
                UPDATE FOLIOS_CAJAS
                SET CONSECUTIVO = CONSECUTIVO + 1
                WHERE CAJA_ID = @CajaId AND TIPO_DOCTO = @TipoDocto
                RETURNING CONSECUTIVO, SERIE",
                new { CajaId = cajaId, TipoDocto = tipoDocto },
                transaction: transaction);

            if (reservado != null)
            {
                int consecutivo = Convert.ToInt32(reservado.CONSECUTIVO);
                string serie = Convert.ToString(reservado.SERIE) ?? "";
                string folio = FormatearFolio(serie, consecutivo - 1);

                // Self-heal: si el contador quedó desfasado (el folio ya fue
                // emitido antes), alinear CONSECUTIVO = MAX(folio emitido) + 1
                // y reintentar. Chequeo por índice DOCTOS_PV_AK1
                // (TIPO_DOCTO, FOLIO, APLICADO): el mismo que rechazaría el INSERT.
                bool colision = await connection.ExecuteScalarAsync<int>(@"
                    SELECT 1 FROM DOCTOS_PV
                    WHERE TIPO_DOCTO = @TipoDocto AND FOLIO = @Folio AND APLICADO = 'S'
                    ROWS 1",
                    new { TipoDocto = tipoDocto, Folio = folio },
                    transaction: transaction) == 1;

                if (!colision)
                    return folio;

                _logger.LogWarning(
                    "[FOLIOS] Contador desfasado en caja {CajaId} tipo {Tipo}: folio {Folio} ya existe. Recalculando a MAX(folio)+1.",
                    cajaId, tipoDocto, folio);

                int maxFolio = await ObtenerMaxFolioNumericoAsync(
                    connection, transaction, cajaId, tipoDocto, serie);

                await connection.ExecuteAsync(@"
                    UPDATE FOLIOS_CAJAS
                    SET CONSECUTIVO = @MaxFolio + 1
                    WHERE CAJA_ID = @CajaId AND TIPO_DOCTO = @TipoDocto",
                    new { MaxFolio = maxFolio, CajaId = cajaId, TipoDocto = tipoDocto },
                    transaction: transaction);

                continue;
            }

            // La caja no tiene fila para este tipo: crear el bloque espejo.
            await CrearBloqueFoliosAsync(connection, transaction, cajaId);
        }

        throw new FolioSinSeriesException(
            $"No se pudo asignar un folio para la caja {cajaId} y tipo '{tipoDocto}'. Verifica la configuración de folios en Microsip.");
    }

    /// <summary>
    /// Máximo sufijo numérico emitido por (caja, tipo, serie) en DOCTOS_PV.
    /// Solo se recorre en el caso raro de contador desfasado.
    /// </summary>
    private async Task<int> ObtenerMaxFolioNumericoAsync(
        FbConnection connection, FbTransaction transaction, int cajaId, string tipoDocto, string serie)
    {
        var folios = await connection.QueryAsync<string>(@"
            SELECT FOLIO FROM DOCTOS_PV
            WHERE TIPO_DOCTO = @TipoDocto AND CAJA_ID = @CajaId
              AND FOLIO STARTING WITH @Serie AND APLICADO = 'S'",
            new { TipoDocto = tipoDocto, CajaId = cajaId, Serie = serie },
            transaction: transaction);

        int max = 0;
        foreach (string folio in folios)
        {
            string sufijo = folio.Substring(serie.Length);
            if (sufijo.Length == 0 || !sufijo.All(char.IsDigit))
                continue;
            if (int.TryParse(sufijo, out int n) && n > max)
                max = n;
        }
        return max;
    }

    private async Task CrearBloqueFoliosAsync(
        FbConnection connection, FbTransaction transaction, int cajaId)
    {
        if (!_autoCrearFolios)
        {
            throw new FolioAutoCreacionDeshabilitadaException(
                $"La caja {cajaId} no tiene folios configurados. Configura la caja en Microsip o activa 'AutoCrearFolios' en el servidor.");
        }

        string inicio = await ObtenerSiguienteSerieInicioAsync(connection, transaction);

        try
        {
            foreach (var fila in _bloque)
            {
                string serie = DesplazarSerie(inicio, fila.DesplazamientoSerie);
                await connection.ExecuteAsync(@"
                    INSERT INTO FOLIOS_CAJAS (CAJA_ID, TIPO_DOCTO, SERIE, CONSECUTIVO)
                    VALUES (@CajaId, @TipoDocto, @Serie, 1)",
                    new { CajaId = cajaId, TipoDocto = fila.Tipo, Serie = serie },
                    transaction: transaction);
            }

            _logger.LogInformation(
                "[FOLIOS] Caja {CajaId}: bloque de folios creado automaticamente ({Inicio}..{Fin})",
                cajaId, inicio, DesplazarSerie(inicio, 6));
        }
        catch (FbException ex) when (EsViolacionLlavePrimaria(ex))
        {
            // Otro proceso (Microsip u otra instancia) ya creó el bloque.
            _logger.LogInformation("[FOLIOS] Caja {CajaId}: el bloque ya existia; se reutiliza.", cajaId);
        }
    }

    private async Task<string> ObtenerSiguienteSerieInicioAsync(
        FbConnection connection, FbTransaction transaction)
    {
        string? maxSerie = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT MAX(SERIE) FROM FOLIOS_CAJAS",
            transaction: transaction);

        if (string.IsNullOrWhiteSpace(maxSerie))
            return "AA";

        // Cada caja consume un bloque de 8 filas pero solo avanza 1 serie:
        // el siguiente bloque arranca en MAX(SERIE) - 5 (evidencia en BD).
        return DesplazarSerie(maxSerie, -5);
    }

    public async Task<int> RepararContadoresAsync()
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = await connection.BeginTransactionAsync();

        var filas = (await connection.QueryAsync(@"
            SELECT CAJA_ID, TIPO_DOCTO, SERIE, CONSECUTIVO
            FROM FOLIOS_CAJAS
            WHERE TIPO_DOCTO IN ('V', 'P')",
            transaction: transaction))
            .Select(f => new FolioFila(
                Convert.ToInt32(f.CAJA_ID),
                Convert.ToString(f.TIPO_DOCTO) ?? "",
                Convert.ToString(f.SERIE) ?? "",
                Convert.ToInt32(f.CONSECUTIVO ?? 0)))
            .ToList();

        var docs = (await connection.QueryAsync(@"
            SELECT CAJA_ID, TIPO_DOCTO, FOLIO
            FROM DOCTOS_PV
            WHERE TIPO_DOCTO IN ('V', 'P') AND CAJA_ID > 0 AND FOLIO IS NOT NULL",
            transaction: transaction))
            .Select(d => (CajaId: (int)d.CAJA_ID,
                          Tipo: (string)d.TIPO_DOCTO,
                          Folio: (string)d.FOLIO))
            .ToList();

        // Máximo folio emitido por (caja, tipo), solo sufijos numéricos válidos.
        var maxPorFila = new Dictionary<(int CajaId, string Tipo), int>();
        foreach (var (cajaId, tipo, folio) in docs)
        {
            var fila = filas.FirstOrDefault(f =>
                f.CajaId == cajaId && f.Tipo == tipo && folio.StartsWith(f.Serie));
            if (fila == null) continue;

            string sufijo = folio.Substring(fila.Serie.Length);
            if (sufijo.Length == 0 || !sufijo.All(char.IsDigit)) continue;

            int numero = int.Parse(sufijo);
            var clave = (cajaId, tipo);
            if (!maxPorFila.TryGetValue(clave, out int actual) || numero > actual)
                maxPorFila[clave] = numero;
        }

        // Respaldo antes de modificar.
        var respaldo = filas.Select(f => new
        {
            f.CajaId, f.Tipo, f.Serie, f.Consecutivo,
            MaxFolioEmitido = maxPorFila.TryGetValue((f.CajaId, f.Tipo), out int max) ? max : 0
        }).ToList();
        string rutaRespaldo = GuardarRespaldo(respaldo);

        int corregidas = 0;
        foreach (var fila in filas)
        {
            if (!maxPorFila.TryGetValue((fila.CajaId, fila.Tipo), out int maxFolio))
                continue;

            // Inconsistente solo si el contador apunta a un folio ya emitido.
            if (fila.Consecutivo > maxFolio)
                continue;

            int nuevo = maxFolio + 1;
            await connection.ExecuteAsync(@"
                UPDATE FOLIOS_CAJAS SET CONSECUTIVO = @Nuevo
                WHERE CAJA_ID = @CajaId AND TIPO_DOCTO = @TipoDocto",
                new { Nuevo = nuevo, fila.CajaId, TipoDocto = fila.Tipo },
                transaction: transaction);

            _logger.LogWarning(
                "[FOLIOS] Reparacion: caja {CajaId} tipo {Tipo} serie {Serie}: CONSECUTIVO {Antes} -> {Despues} (max folio emitido {Max})",
                fila.CajaId, fila.Tipo, fila.Serie, fila.Consecutivo, nuevo, maxFolio);
            corregidas++;
        }

        await transaction.CommitAsync();

        _logger.LogInformation(
            "[FOLIOS] Reparacion completada: {Corregidas} fila(s) corregida(s). Respaldo en {Ruta}",
            corregidas, rutaRespaldo);

        return corregidas;
    }

    private string GuardarRespaldo(object datos)
    {
        string carpeta = Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(carpeta);
        string ruta = Path.Combine(carpeta, $"respaldo_contadores_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(ruta, JsonSerializer.Serialize(datos, new JsonSerializerOptions { WriteIndented = true }));
        return ruta;
    }

    private static string FormatearFolio(string serie, int consecutivo)
    {
        int digitos = 9 - serie.Length;
        if (consecutivo < 0) consecutivo = 0;
        return serie + consecutivo.ToString().PadLeft(digitos, '0');
    }

    /// <summary>
    /// Desplaza una serie alfabética (A-Z, con acarreo): "AA"+1 = "AB",
    /// "AZ"+1 = "BA", "LO"-5 = "LJ".
    /// </summary>
    private static string DesplazarSerie(string serie, int pasos)
    {
        if (string.IsNullOrWhiteSpace(serie))
            return "AA";

        var caracteres = serie.ToUpperInvariant().ToCharArray();
        int direccion = pasos >= 0 ? 1 : -1;

        for (int p = 0; p < Math.Abs(pasos); p++)
        {
            int i = caracteres.Length - 1;
            while (i >= 0)
            {
                if (direccion > 0)
                {
                    if (caracteres[i] < 'Z') { caracteres[i]++; break; }
                    caracteres[i] = 'A';
                }
                else
                {
                    if (caracteres[i] > 'A') { caracteres[i]--; break; }
                    caracteres[i] = 'Z';
                }
                i--;
            }
            if (i < 0 && direccion > 0)
            {
                // Desbordamiento (ZZ -> AAA): extremadamente raro, se expande.
                return new string(caracteres) + "A";
            }
        }
        return new string(caracteres);
    }

    private static bool EsViolacionLlavePrimaria(FbException ex)
    {
        // GDSCODE 803 = unique_key_violation (PK/unique)
        if (ex.ErrorCode == 803)
            return true;

        if (ex.Errors != null)
        {
            foreach (var error in ex.Errors)
            {
                if (error.Number == 803)
                    return true;
            }
        }
        return false;
    }

    private sealed record FolioFila(int CajaId, string Tipo, string Serie, int Consecutivo);
}
