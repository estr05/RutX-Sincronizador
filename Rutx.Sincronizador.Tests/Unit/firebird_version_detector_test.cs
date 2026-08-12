using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Pruebas del detector de version Firebird por header .fdb.
/// Usa headers sinteticos: byte 0 = 1 (header page), ODS major en 18-19
/// (con flag Firebird 0x8000) y ODS minor en el offset correcto por familia
/// (62 para ODS <= 11, 64 para ODS 12+). Esto fija los offsets y el mapa,
/// evitando que deriven del detector del proyecto Admin.
/// </summary>
public class FirebirdVersionDetectorWebTests
{
    private static string CrearFdbSintetica(int major, int minor, int offsetMinor)
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"sintetica_{major}_{minor}_{Guid.NewGuid():N}.fdb");
        var bytes = new byte[128];
        bytes[0] = 1; // page_type = header page
        var ods = BitConverter.GetBytes((ushort)(major | 0x8000));
        bytes[18] = ods[0];
        bytes[19] = ods[1];
        var minorBytes = BitConverter.GetBytes((ushort)minor);
        bytes[offsetMinor] = minorBytes[0];
        bytes[offsetMinor + 1] = minorBytes[1];
        File.WriteAllBytes(ruta, bytes);
        return ruta;
    }

    [Fact]
    public void Detecta_ODS_12_0_Como_Firebird_3_0()
    {
        var ruta = CrearFdbSintetica(12, 0, 64);
        try
        {
            var (major, minor) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(12, major);
            Assert.Equal(0, minor);
            Assert.Equal("Firebird 3.0", FirebirdVersionDetector.NombreFirebirdDeOds(major, minor));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void Detecta_ODS_13_1_Como_Firebird_5_0()
    {
        var ruta = CrearFdbSintetica(13, 1, 64);
        try
        {
            var (major, minor) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(13, major);
            Assert.Equal(1, minor);
            Assert.Equal("Firebird 5.0", FirebirdVersionDetector.NombreFirebirdDeOds(major, minor));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void Detecta_ODS_13_0_Como_Firebird_4_0()
    {
        var ruta = CrearFdbSintetica(13, 0, 64);
        try
        {
            var (major, minor) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(13, major);
            Assert.Equal(0, minor);
            Assert.Equal("Firebird 4.0", FirebirdVersionDetector.NombreFirebirdDeOds(major, minor));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void Detecta_ODS_11_2_Con_Minor_En_Offset_62()
    {
        var ruta = CrearFdbSintetica(11, 2, 62);
        try
        {
            var (major, minor) = FirebirdVersionDetector.LeerOdsDeArchivo(ruta);
            Assert.Equal(11, major);
            Assert.Equal(2, minor);
            Assert.Equal("Firebird 2.5", FirebirdVersionDetector.NombreFirebirdDeOds(major, minor));
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public void ODS_13_Con_Minor_Desconocido_Usa_La_Familia_Mas_Nueva()
    {
        // (13, 9) no esta en el mapa -> debe caer a la familia 13 mas nueva (5.0)
        Assert.Equal("Firebird 5.0", FirebirdVersionDetector.NombreFirebirdDeOds(13, 9));
    }

    [Fact]
    public void Archivo_Inexistente_Devuelve_Cero()
    {
        var (major, minor) = FirebirdVersionDetector.LeerOdsDeArchivo(@"C:\ruta\que\no\existe\NADA.fdb");
        Assert.Equal(0, major);
        Assert.Equal(0, minor);
    }
}
