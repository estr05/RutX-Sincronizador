using System;
using System.Collections.Generic;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

public class InventarioReconciliacionTest
{
    private const int AlmacenGeneral = 19;
    private const int AlmacenCoche = 11206; // RUTXALMACEN01
    private const int ConceptoSaldoInicial = 28;
    private static readonly ISet<int> ConceptosSeed = new HashSet<int> { ConceptoSaldoInicial };

    private static MovimientoInventarioDto Linea(
        long docto, int articulo, string tipo, int almacen, decimal unidades,
        int? destino = null, int concepto = ConceptoSaldoInicial, string nombreArticulo = "Producto")
        => new()
        {
            DoctoInId = docto,
            Fecha = new DateTime(2026, 8, 12),
            ArticuloId = articulo,
            ArticuloNombre = nombreArticulo,
            ConceptoInId = concepto,
            ConceptoNombre = concepto == ConceptoSaldoInicial ? "Saldo inicial" : "Traspaso (salida)",
            TipoMovto = tipo,
            AlmacenId = almacen,
            AlmacenDestinoId = destino,
            Unidades = unidades,
            Aplicado = "S",
            Cancelado = "N"
        };

    private static List<OperacionInventarioDto> Clasificar(
        IEnumerable<MovimientoInventarioDto> lineas,
        Func<int, int, decimal> existencias)
        => InventarioReconciliacionService.Clasificar(lineas, existencias, AlmacenGeneral, ConceptosSeed);

    private static Func<int, int, decimal> ExistenciasNulas() => (_, _) => 0m;

    [Fact]
    public void ParS_E_MismoDoctoYArticulo_SeClasificaComoTransfer()
    {
        // Misma forma que el DOCTO 711255 real: S en Almacen general + E en el coche.
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711255, 11126, "S", AlmacenGeneral, 1m, destino: AlmacenCoche),
            Linea(711255, 11126, "E", AlmacenCoche, 1m, destino: AlmacenCoche),
        };

        // Existencia suficiente en el origen (regla respetada).
        decimal Exist(int alm, int art) => alm == AlmacenGeneral && art == 11126 ? 5m : 0m;

        var ops = Clasificar(lineas, Exist);

        var transfer = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Transfer, transfer.Tipo);
        Assert.Equal(711255, transfer.DoctoInId);
        Assert.Equal(11126, transfer.ArticuloId);
        Assert.Equal(AlmacenGeneral, transfer.AlmacenOrigenId);
        Assert.Equal(AlmacenCoche, transfer.AlmacenDestinoId);
        Assert.Equal(1m, transfer.Unidades);
    }

    [Fact]
    public void SaldoInicial_SinPareja_ProductoNoRastreado_EsSeed()
    {
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711253, 7807, "E", AlmacenCoche, 2m),
        };

        var ops = Clasificar(lineas, ExistenciasNulas());

        var seed = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Seed, seed.Tipo);
        Assert.Equal(2m, seed.Unidades);
    }

    [Fact]
    public void SaldoInicial_SobreProductoRastreado_EsAnomalia()
    {
        // El producto SÍ tiene existencias en el Almacen general -> debió ser Traspaso.
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711253, 7807, "E", AlmacenCoche, 2m),
        };

        decimal Exist(int alm, int art) => alm == AlmacenGeneral && art == 7807 ? 50m : 0m;

        var ops = Clasificar(lineas, Exist);

        var anomalia = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Anomalia, anomalia.Tipo);
        Assert.Contains("Traspaso", anomalia.Motivo);
    }

    [Fact]
    public void EntradaSinPareja_ConceptoNoSeed_EsAnomalia()
    {
        // Entrada con concepto distinto a Saldo inicial (ej. Compra) sin pareja.
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711300, 100, "E", AlmacenCoche, 5m, concepto: 20),
        };

        var ops = Clasificar(lineas, ExistenciasNulas());

        var anomalia = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Anomalia, anomalia.Tipo);
        Assert.Contains("pareja", anomalia.Motivo);
    }

    [Fact]
    public void SalidaSinPareja_EsAnomalia()
    {
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711262, 11126, "S", AlmacenCoche, 10m),
        };

        var ops = Clasificar(lineas, ExistenciasNulas());

        var anomalia = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Anomalia, anomalia.Tipo);
        Assert.Contains("Salida sin su entrada", anomalia.Motivo);
    }

    [Fact]
    public void TransferQueExcedeExistenciaDelOrigen_EsAnomalia()
    {
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711255, 11126, "S", AlmacenGeneral, 10m, destino: AlmacenCoche),
            Linea(711255, 11126, "E", AlmacenCoche, 10m, destino: AlmacenCoche),
        };

        decimal Exist(int alm, int art) => alm == AlmacenGeneral && art == 11126 ? 2m : 0m;

        var ops = Clasificar(lineas, Exist);

        var anomalia = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Anomalia, anomalia.Tipo);
        Assert.Contains("excede", anomalia.Motivo);
    }

    [Fact]
    public void TransferConExistenciaSuficiente_EsTransferNoAnomalia()
    {
        var lineas = new List<MovimientoInventarioDto>
        {
            Linea(711255, 11126, "S", AlmacenGeneral, 2m, destino: AlmacenCoche),
            Linea(711255, 11126, "E", AlmacenCoche, 2m, destino: AlmacenCoche),
        };

        decimal Exist(int alm, int art) => alm == AlmacenGeneral && art == 11126 ? 5m : 0m;

        var ops = Clasificar(lineas, Exist);

        var transfer = Assert.Single(ops);
        Assert.Equal(TipoOperacionInventario.Transfer, transfer.Tipo);
    }
}
