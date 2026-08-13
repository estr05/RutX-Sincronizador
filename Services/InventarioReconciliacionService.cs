// ============================================================================
// ARCHIVO: InventarioReconciliacionService.cs
// PROPOSITO: Reconciliacion de movimientos de inventario de Microsip.
//
// REGLA DE NEGOCIO (carga del coche del vendedor):
//   1. Cargar el coche (RUTXALMACENxx) con productos que ya existen en el
//      Almacen general DEBE hacerse via TRASPASO: en Microsip eso genera un
//      DOCTO con un par S (origen) + E (destino) del mismo articulo/cantidad.
//      Se valida que el traspaso no exceda la existencia disponible del origen.
//   2. "Saldo inicial" (entrada sin pareja) queda reservado para productos
//      SIN rastreo en el Almacen general. Si se usa sobre un producto con
//      existencias en el general, es una anomalia.
//   3. Cualquier S o E sin su pareja en el mismo DOCTO se marca como
//      ANOMALIA y se reporta (no se aplica a ciegas).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public class InventarioReconciliacionService : IInventarioReconciliacionService
{
    private readonly string _connectionString;
    private readonly int _almacenGeneralId;
    private readonly HashSet<int> _conceptosSeed;
    private readonly ILogger<InventarioReconciliacionService> _logger;

    private ResultadoReconciliacionDto? _ultimoResultado;

    public InventarioReconciliacionService(
        IConfiguration configuration,
        ILogger<InventarioReconciliacionService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException(
                "No se encontro la cadena de conexion 'FirebirdConnection' en appsettings.");

        _almacenGeneralId = configuration.GetValue<int>("InventarioReconciliacion:AlmacenGeneralId", 19);

        var seedConfig = configuration.GetSection("InventarioReconciliacion:ConceptosSeed").Get<int[]>();
        _conceptosSeed = seedConfig != null && seedConfig.Length > 0
            ? new HashSet<int>(seedConfig)
            : new HashSet<int> { 28 }; // 28 = "Saldo inicial"
    }

    public ResultadoReconciliacionDto? UltimoResultado => _ultimoResultado;

    /// <summary>
    /// Lee los movimientos aplicados (APLICADO='S', CANCELADO='N') desde [desde]
    /// y las existencias vigentes (SALDOS_IN), clasifica y reporta.
    /// </summary>
    public async Task<ResultadoReconciliacionDto> ReconciliarAsync(DateTime desde)
    {
        using var connection = new FbConnection(_connectionString);
        await connection.OpenAsync();

        var lineas = (await connection.QueryAsync<MovimientoInventarioDto>(MovimientosSql,
            new { Desde = desde })).ToList();

        var saldos = (await connection.QueryAsync<SaldosRow>(SaldosSql)).ToList();
        var existencias = saldos.ToDictionary(
            k => (k.AlmacenId, k.ArticuloId), v => v.Existencias);

        decimal Existencia(int almacenId, int articuloId) =>
            existencias.TryGetValue((almacenId, articuloId), out var e) ? e : 0m;

        var operaciones = Clasificar(lineas, Existencia, _almacenGeneralId, _conceptosSeed);

        var resultado = new ResultadoReconciliacionDto
        {
            GeneradoEn = DateTime.Now,
            Desde = desde,
            Operaciones = operaciones,
            AnomaliasList = operaciones.Where(o => o.Tipo == TipoOperacionInventario.Anomalia).ToList(),
            Transferencias = operaciones.Count(o => o.Tipo == TipoOperacionInventario.Transfer),
            Semillas = operaciones.Count(o => o.Tipo == TipoOperacionInventario.Seed),
            Anomalias = operaciones.Count(o => o.Tipo == TipoOperacionInventario.Anomalia)
        };

        _ultimoResultado = resultado;
        return resultado;
    }

    // =========================================================================
    // CLASIFICACION (pura, unit-testable)
    // =========================================================================

    /// <summary>
    /// Clasifica lineas de movimiento agrupadas por DOCTO y articulo:
    ///   - par S + E del mismo articulo con unidades iguales -> TRANSFER
    ///   - solo E con concepto seed (Saldo inicial)              -> SEED
    ///   - cualquier otro caso                                   -> ANOMALIA
    /// Luego valida la regla de negocio (existencia en origen del traspaso
    /// y producto no rastreado para el saldo inicial).
    /// </summary>
    public static List<OperacionInventarioDto> Clasificar(
        IEnumerable<MovimientoInventarioDto> lineas,
        Func<int, int, decimal> existenciasPorAlmacen,
        int almacenGeneralId,
        ISet<int> conceptosSeed)
    {
        var resultado = new List<OperacionInventarioDto>();

        foreach (var docto in lineas.GroupBy(l => l.DoctoInId))
        {
            foreach (var articulo in docto.GroupBy(l => l.ArticuloId))
            {
                var sLineas = articulo.Where(l => l.TipoMovto == "S").ToList();
                var eLineas = articulo.Where(l => l.TipoMovto == "E").ToList();
                var primera = articulo.First();

                bool esParTransfer =
                    sLineas.Count > 0 && eLineas.Count > 0 &&
                    sLineas.Count == eLineas.Count &&
                    sLineas.Sum(x => x.Unidades) == eLineas.Sum(x => x.Unidades);

                if (esParTransfer)
                {
                    var unidades = sLineas.Sum(x => x.Unidades);
                    var origen = sLineas.First().AlmacenId;
                    var destino = eLineas.First().AlmacenId > 0
                        ? eLineas.First().AlmacenId
                        : (primera.AlmacenDestinoId ?? 0);

                    var disponible = existenciasPorAlmacen(origen, articulo.Key);
                    if (disponible < unidades)
                    {
                        resultado.Add(new OperacionInventarioDto
                        {
                            DoctoInId = docto.Key,
                            Fecha = primera.Fecha,
                            Tipo = TipoOperacionInventario.Anomalia,
                            ArticuloId = articulo.Key,
                            ArticuloNombre = primera.ArticuloNombre,
                            Unidades = unidades,
                            AlmacenOrigenId = origen,
                            AlmacenDestinoId = destino,
                            ConceptoInId = primera.ConceptoInId,
                            ConceptoNombre = primera.ConceptoNombre,
                            Motivo = $"Traspaso excede la existencia disponible del origen " +
                                     $"(disponible: {disponible:0.###})."
                        });
                        continue;
                    }

                    resultado.Add(new OperacionInventarioDto
                    {
                        DoctoInId = docto.Key,
                        Fecha = primera.Fecha,
                        Tipo = TipoOperacionInventario.Transfer,
                        ArticuloId = articulo.Key,
                        ArticuloNombre = primera.ArticuloNombre,
                        Unidades = unidades,
                        AlmacenOrigenId = origen,
                        AlmacenDestinoId = destino,
                        ConceptoInId = primera.ConceptoInId,
                        ConceptoNombre = primera.ConceptoNombre
                    });
                    continue;
                }

                // Solo entradas sin pareja: SEED solo si el concepto es de siembra
                // (ej. "Saldo inicial", 28) y el producto NO esta rastreado en el
                // almacen general (debió cargarse via Traspaso).
                if (sLineas.Count == 0 && eLineas.Count > 0 &&
                    conceptosSeed.Contains(primera.ConceptoInId))
                {
                    var rastreado = existenciasPorAlmacen(almacenGeneralId, articulo.Key) > 0;
                    if (rastreado)
                    {
                        resultado.Add(new OperacionInventarioDto
                        {
                            DoctoInId = docto.Key,
                            Fecha = primera.Fecha,
                            Tipo = TipoOperacionInventario.Anomalia,
                            ArticuloId = articulo.Key,
                            ArticuloNombre = primera.ArticuloNombre,
                            Unidades = eLineas.Sum(x => x.Unidades),
                            AlmacenOrigenId = primera.AlmacenId,
                            ConceptoInId = primera.ConceptoInId,
                            ConceptoNombre = primera.ConceptoNombre,
                            Motivo = "Saldo inicial sobre producto rastreado en el Almacén general: " +
                                     "debió cargarse vía Traspaso."
                        });
                        continue;
                    }

                    resultado.Add(new OperacionInventarioDto
                    {
                        DoctoInId = docto.Key,
                        Fecha = primera.Fecha,
                        Tipo = TipoOperacionInventario.Seed,
                        ArticuloId = articulo.Key,
                        ArticuloNombre = primera.ArticuloNombre,
                        Unidades = eLineas.Sum(x => x.Unidades),
                        AlmacenOrigenId = primera.AlmacenId,
                        ConceptoInId = primera.ConceptoInId,
                        ConceptoNombre = primera.ConceptoNombre
                    });
                    continue;
                }

                // Cualquier otra combinacion: S sin pareja, E sin pareja (no seed),
                // o S/E desbalanceados -> ANOMALIA.
                string motivo = sLineas.Count == 0
                    ? "Entrada sin su salida de pareja en el mismo documento."
                    : eLineas.Count == 0
                        ? "Salida sin su entrada de pareja en el mismo documento."
                        : "Pareja S/E desbalanceada en el mismo documento (cantidades distintas).";

                resultado.Add(new OperacionInventarioDto
                {
                    DoctoInId = docto.Key,
                    Fecha = primera.Fecha,
                    Tipo = TipoOperacionInventario.Anomalia,
                    ArticuloId = articulo.Key,
                    ArticuloNombre = primera.ArticuloNombre,
                    Unidades = Math.Max(sLineas.Sum(x => x.Unidades), eLineas.Sum(x => x.Unidades)),
                    AlmacenOrigenId = sLineas.Count > 0 ? sLineas.First().AlmacenId : primera.AlmacenId,
                    AlmacenDestinoId = sLineas.Count > 0 && eLineas.Count > 0
                        ? (int?)eLineas.First().AlmacenId
                        : primera.AlmacenDestinoId,
                    ConceptoInId = primera.ConceptoInId,
                    ConceptoNombre = primera.ConceptoNombre,
                    Motivo = motivo
                });
            }
        }

        return resultado;
    }

    // =========================================================================
    // SQL
    // =========================================================================

    private const string MovimientosSql = @"
        SELECT
            d.DOCTO_IN_ID AS DoctoInId,
            h.FECHA AS Fecha,
            d.ARTICULO_ID AS ArticuloId,
            a.NOMBRE AS ArticuloNombre,
            h.CONCEPTO_IN_ID AS ConceptoInId,
            c.NOMBRE AS ConceptoNombre,
            d.TIPO_MOVTO AS TipoMovto,
            d.ALMACEN_ID AS AlmacenId,
            h.ALMACEN_DESTINO_ID AS AlmacenDestinoId,
            d.UNIDADES AS Unidades,
            h.APLICADO AS Aplicado,
            h.CANCELADO AS Cancelado
        FROM DOCTOS_IN_DET d
        INNER JOIN DOCTOS_IN h ON h.DOCTO_IN_ID = d.DOCTO_IN_ID
        LEFT JOIN ARTICULOS a ON a.ARTICULO_ID = d.ARTICULO_ID
        LEFT JOIN CONCEPTOS_IN c ON c.CONCEPTO_IN_ID = h.CONCEPTO_IN_ID
        WHERE h.FECHA >= @Desde
          AND h.CANCELADO = 'N'
          AND h.APLICADO = 'S'
        ORDER BY h.DOCTO_IN_ID, d.ARTICULO_ID, d.TIPO_MOVTO";

    /// <summary>Existencias vigentes por almacen/articulo (periodo mas reciente de SALDOS_IN).</summary>
    private const string SaldosSql = @"
        SELECT
            s.ALMACEN_ID AS AlmacenId,
            s.ARTICULO_ID AS ArticuloId,
            (s.ENTRADAS_UNIDADES - s.SALIDAS_UNIDADES) AS Existencias
        FROM SALDOS_IN s
        WHERE s.ANO = (SELECT MAX(x.ANO) FROM SALDOS_IN x
                       WHERE x.ALMACEN_ID = s.ALMACEN_ID AND x.ARTICULO_ID = s.ARTICULO_ID)
          AND s.MES = (SELECT MAX(x.MES) FROM SALDOS_IN x
                       WHERE x.ALMACEN_ID = s.ALMACEN_ID AND x.ARTICULO_ID = s.ARTICULO_ID
                         AND x.ANO = s.ANO)";

    private class SaldosRow
    {
        public int AlmacenId { get; set; }
        public int ArticuloId { get; set; }
        public decimal Existencias { get; set; }
    }
}
