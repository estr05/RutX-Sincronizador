using Rutx.Sincronizador.Admin;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Tests del detector de version de Firebird.
///
/// Los .fdb son sinteticos: solo se escribe el header page real (page_type=0x01,
/// ODS major con el flag Firebird 0x8000 en bytes 18-19 y el minor en el offset
/// que usa cada familia de ODS). Asi se valida la logica de offsets sin depender
/// de un servidor Firebird.
/// </summary>
public class FirebirdVersionDetectorTests
{
    // ---- Helpers --------------------------------------------------------

    /// <summary>
    /// Escribe un .fdb sintetico con el header de una BD Firebird:
    ///   byte 0-1: page_type=0x01, flags=0x00
    ///   bytes 16-17: page_size (8192)
    ///   bytes 18-19: ODS major little-endian con flag 0x8000
    ///   minor en el offset correspondiente a la familia (62 / 64 / 20)
    /// </summary>
    private static string CrearFdbSintetico(int odsMajor, int odsMinor, int? minorOriginal = null)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"fb_sintetico_{odsMajor}_{odsMinor}_{Guid.NewGuid():N}.fdb");
        var header = new byte[200];
        header[0] = 0x01;                 // page type: header page
        header[1] = 0x00;                 // flags
        header[16] = 0x00;
        header[17] = 0x20;                // page size 8192 (LE)

        ushort odsRaw = (ushort)(0x8000 | odsMajor);
        header[18] = (byte)(odsRaw & 0xFF);
        header[19] = (byte)(odsRaw >> 8);

        // El minor esta en 62 solo en la familia 1.x/2.x; en ODS 12 y 13
        // (Firebird 3.0/4.0/5.0) esta en 64 (verificado en ods.h y gstat).
        int offsetMinor = odsMajor <= 11 ? 62 : 64;
        header[offsetMinor] = (byte)(odsMinor & 0xFF);
        header[offsetMinor + 1] = (byte)(odsMinor >> 8);

        // hdr_ods_minor_original (offset 64 en la familia 1.x/2.x): por defecto
        // igual al minor; se puede sobreescribir para simular BDs actualizadas.
        if (odsMajor <= 11 && minorOriginal is { } orig)
        {
            header[64] = (byte)(orig & 0xFF);
            header[65] = (byte)(orig >> 8);
        }

        File.WriteAllBytes(ruta, header);
        return ruta;
    }

    // ---- LeerOdsDeArchivo: offsets por familia --------------------------

    [Theory]
    [InlineData(10, 0)]   // Firebird 1.0
    [InlineData(10, 1)]   // Firebird 1.5
    [InlineData(11, 0)]   // Firebird 2.0
    [InlineData(11, 1)]   // Firebird 2.1
    [InlineData(11, 2)]   // Firebird 2.5
    [InlineData(12, 0)]   // Firebird 3.0
    [InlineData(13, 0)]   // Firebird 4.0
    [InlineData(13, 1)]   // Firebird 5.0 (ODS real de CRUZROJASCLC)
    [InlineData(13, 3)]   // minor raro dentro de la familia 13
    public void LeerOds_DevuelveMajorYMinorCorrectos(int major, int minor)
    {
        var ruta = CrearFdbSintetico(major, minor);
        try
        {
            var (m, mn) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(major, m);
            Assert.Equal(minor, mn);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void LeerOds_BdActualizadaDe2_1_A2_5_LeeElMinorRealNoElOriginal()
    {
        // BD creada en Firebird 2.1 y actualizada a 2.5:
        //   hdr_ods_minor (offset 62) = 2
        //   hdr_ods_minor_original (offset 64) = 1  <- este es el que leia el codigo viejo
        var ruta = CrearFdbSintetico(11, 2, minorOriginal: 1);
        try
        {
            var (m, mn) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(11, m);
            Assert.Equal(2, mn);   // debe leer el minor REAL (62), no el original (64)
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void LeerOds_ArchivoQueNoEsFdb_DevuelveCero()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"no_fdb_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(ruta, new byte[100]); // page_type = 0x00
            var (m, mn) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(0, m);
            Assert.Equal(0, mn);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void LeerOds_RutaInexistente_DevuelveCero()
    {
        var (m, mn) = FirebirdVersionDetector.LeerOdsDeArchivo(
            Path.Combine(Path.GetTempPath(), "no_existe_12345.fdb"));
        Assert.Equal(0, m);
        Assert.Equal(0, mn);
    }

    // ---- Detectar sin conexion: mapa ODS -> version ----------------------

    [Theory]
    [InlineData(10, 0, "1.0", "Firebird 1.0", false)]
    [InlineData(10, 1, "1.5", "Firebird 1.5", false)]
    [InlineData(11, 0, "2.0", "Firebird 2.0", true)]
    [InlineData(11, 1, "2.1", "Firebird 2.1", true)]
    [InlineData(11, 2, "2.5", "Firebird 2.5", true)]
    [InlineData(12, 0, "3.0", "Firebird 3.0", true)]
    [InlineData(13, 0, "4.0", "Firebird 4.0", true)]
    [InlineData(13, 1, "5.0", "Firebird 5.0", true)]
    public void Detectar_SinConexion_MapeaOdsExacto(
        int major, int minor, string version, string nombre, bool soportada)
    {
        var ruta = CrearFdbSintetico(major, minor);
        try
        {
            var info = FirebirdVersionDetector.Detectar(ruta); // sin cadena de conexion
            Assert.Equal(major, info.Ods);
            Assert.Equal(minor, info.OdsMinor);
            Assert.Equal(version, info.Version);
            Assert.Equal(nombre, info.Nombre);
            Assert.Equal(soportada, info.Soportada);
            Assert.Contains("ODS", info.MetodoDeteccion);
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Theory]
    [InlineData(12, 3, "Firebird 3.0")]   // minor raro -> unica familia 12
    [InlineData(13, 3, "Firebird 5.0")]   // minor raro -> familia mas nueva
    [InlineData(13, 2, "Firebird 5.0")]
    public void Detectar_SinConexion_MinorNoMapeado_UsaFamilia(
        int major, int minor, string nombreEsperado)
    {
        var ruta = CrearFdbSintetico(major, minor);
        try
        {
            var info = FirebirdVersionDetector.Detectar(ruta);
            Assert.Equal(nombreEsperado, info.Nombre);
            // Sin SQL no se debe inventar una version exacta que no conocemos
            Assert.True(string.IsNullOrEmpty(info.Version), "La version exacta debe quedar vacia");
        }
        finally
        {
            File.Delete(ruta);
        }
    }
}
