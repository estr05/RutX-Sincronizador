using FirebirdSql.Data.FirebirdClient;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Pruebas del desglose de la cadena de conexion Firebird con
/// FbConnectionStringBuilder: el panel muestra cada componente en su propio
/// campo (ruta BD, usuario, password, host, puerto...). El builder normaliza
/// los aliases del provider (initial catalog, user id, port number,
/// character set...), asi que cualquier formato de cadena debe desglosarse
/// igual.
/// </summary>
public class ConnectionStringTests
{
    [Fact]
    public void Desglosa_La_Cadena_Con_Aliases_Del_Usuario()
    {
        // Formato mostrado por el usuario (aliases del provider)
        var cadena = "initial catalog=\"C:\\Microsip datos\\CHOCOLATES.fdb\";"
                   + "user id=SYSDBA;password=masterkey;data source=localhost;"
                   + "port number=3050;dialect=3;pooling=False;character set=UTF8";

        var b = new FbConnectionStringBuilder(cadena);

        Assert.Equal(@"C:\Microsip datos\CHOCOLATES.fdb", b.Database);
        Assert.Equal("SYSDBA", b.UserID);
        Assert.Equal("masterkey", b.Password);
        Assert.Equal("localhost", b.DataSource);
        Assert.Equal(3050, b.Port);
        Assert.Equal(3, b.Dialect);
        Assert.False(b.Pooling);
        Assert.Equal("UTF8", b.Charset);
    }

    [Fact]
    public void Desglosa_El_Formato_Nativo_De_AppSettings()
    {
        // Formato usado hoy en appsettings.json
        var cadena = "User=SYSDBA;Password=masterkey;Database=C:\\Microsip datos\\CHOCOLATES.fdb;"
                   + "DataSource=localhost;Port=3050;Dialect=3;Pooling=true;";

        var b = new FbConnectionStringBuilder(cadena);

        Assert.Equal(@"C:\Microsip datos\CHOCOLATES.fdb", b.Database);
        Assert.Equal("SYSDBA", b.UserID);
        Assert.Equal(3050, b.Port);
        Assert.Equal(3, b.Dialect);
        Assert.True(b.Pooling);
    }

    [Fact]
    public void Sin_Cadena_Devuelve_Valores_Vacios_Con_Defaults_Del_Provider()
    {
        var b = new FbConnectionStringBuilder("");
        Assert.Equal("", b.Database);
        Assert.Equal("", b.UserID);
        Assert.Equal("", b.DataSource);
        // Defaults del provider Firebird (lo que realmente se usaria)
        Assert.Equal(3050, b.Port);
        Assert.Equal(3, b.Dialect);
    }
}
