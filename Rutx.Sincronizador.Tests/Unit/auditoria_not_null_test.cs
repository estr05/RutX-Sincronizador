using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

public class AuditoriaNotNullTest
{
    [Fact]
    public void SinColumnasSinDefault_DevuelveListaVacia()
    {
        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(Array.Empty<(string, string)>());

        Assert.Empty(resultado);
    }

    [Fact]
    public void ColumnasQueElSyncEscribe_NoSonPeligrosas()
    {
        // Mismas columnas reales detectadas en CHOCOLATES/CRUZROJAS (todas escritas por el sync)
        var columnas = new (string, string)[]
        {
            ("CLIENTES", "CLIENTE_ID"),
            ("CLIENTES", "NOMBRE"),
            ("DOCTOS_PV", "DOCTO_PV_ID"),
            ("DOCTOS_PV", "TIPO_DOCTO"),
            ("DOCTOS_PV_DET", "ARTICULO_ID"),
            ("DOCTOS_CC", "DOCTO_CC_ID"),
            ("DOCTOS_ENTRE_SIS", "CLAVE_SIS_FTE"),
            ("IMPUESTOS_DOCTOS_PV", "IMPUESTO_ID"),
            ("IMPUESTOS_DOCTOS_PV_DET", "TIPO_CALC"),
            ("DOCTOS_PV_COBROS", "FORMA_COBRO_ID"),
            ("DOCTOS_PV_LIGAS", "DOCTO_PV_LIGA_ID"),
            ("FOLIOS_CAJAS", "SERIE"),
        };

        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(columnas);

        Assert.Equal(10, resultado.Count); // 10 tablas distintas
        Assert.All(resultado, r => Assert.Empty(r.NoEscritas));
    }

    [Fact]
    public void ColumnaNuevaNoEscrita_SeMarcaComoPeligrosa()
    {
        var columnas = new (string, string)[]
        {
            ("DOCTOS_PV", "DOCTO_PV_ID"),
            ("DOCTOS_PV", "COLUMNA_NUEVA_2026"), // una version futura de Microsip podria agregarla
        };

        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(columnas);

        var pv = Assert.Single(resultado);
        Assert.Equal("DOCTOS_PV", pv.Tabla);
        Assert.Equal(new[] { "COLUMNA_NUEVA_2026" }, pv.NoEscritas);
        Assert.Equal(new[] { "COLUMNA_NUEVA_2026", "DOCTO_PV_ID" }, pv.SinDefault);
    }

    [Fact]
    public void TablaDesconocidaParaElSync_MarcaTodasComoPeligrosas()
    {
        var columnas = new (string, string)[] { ("TABLA_FUTURA", "COL1"), ("TABLA_FUTURA", "COL2") };

        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(columnas);

        var t = Assert.Single(resultado);
        Assert.Equal(new[] { "COL1", "COL2" }, t.NoEscritas);
    }

    [Fact]
    public void NormalizaMayusculasYEspacios()
    {
        var columnas = new (string, string)[] { ("  doctos_pv ", "  tipo_docto ") };

        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(columnas);

        var pv = Assert.Single(resultado);
        Assert.Equal("DOCTOS_PV", pv.Tabla);
        Assert.Empty(pv.NoEscritas); // tipo_docto SI esta en la lista del sync
    }

    [Fact]
    public void EsFacGlobalConDefaultDeDominio_NoApareceEnElCheck()
    {
        // ES_FAC_GLOBAL tiene default del dominio SI_NO_N -> la consulta SQL lo excluye.
        // El helper solo recibe lo que devuelve el SQL; aqui verificamos que si llegara
        // (por un bug de dominio), se detectaria porque el sync no la escribe.
        var columnas = new (string, string)[] { ("DOCTOS_PV", "ES_FAC_GLOBAL") };

        var resultado = AuditoriaCompatibilidadService.ClasificarColumnasSinDefault(columnas);

        var pv = Assert.Single(resultado);
        Assert.Equal(new[] { "ES_FAC_GLOBAL" }, pv.NoEscritas);
    }
}
