using System.Text.Json;
using System.Text.Json.Nodes;
using Rutx.Sincronizador.Admin;
using Rutx.Sincronizador.Shared;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Pruebas de las reglas de politica y de la generacion de appsettings del
/// instalador. NO se crea C:\Microsip Extras\ ni se aplican ACLs ni se tocan
/// instalaciones reales: se usan reglas puras y directorios temporales.
/// La instalacion completa (copiar exe, ACLs, auditoria) queda para la prueba
/// manual en Windows.
/// </summary>
public class InstalacionHelperTests
{
    // ==================================================================
    // WebPasswordPolicy (reglas puras)
    // ==================================================================

    [Theory]
    [InlineData("Corta7!")]   // 7 caracteres
    public void PasswordWeb_LongaInsuficiente_EsInvalida(string pass)
    {
        Assert.False(WebPasswordPolicy.EsValida(pass, "admin", out var error));
        Assert.Contains("8 caracteres", error);
    }

    [Fact]
    public void PasswordWeb_Vacia_EsInvalida()
    {
        Assert.False(WebPasswordPolicy.EsValida("", "admin", out var error));
        Assert.Contains("obligatoria", error);
    }

    [Fact]
    public void PasswordWeb_Exactamente_8_EsValida()
    {
        Assert.True(WebPasswordPolicy.EsValida("Clave8x!", "admin", out _));
    }

    [Fact]
    public void PasswordWeb_Admin_EsRechazada()
    {
        // 'admin' es el usuario por defecto del panel y, ademas, con 5 caracteres
        // queda por debajo del minimo de 8: se rechaza por longitud.
        Assert.False(WebPasswordPolicy.EsValida("admin", "panelsuper", out var error));
        Assert.Contains("8 caracteres", error);
    }

    [Fact]
    public void PasswordWeb_Igual_Al_Usuario_Del_Panel_EsInvalida()
    {
        Assert.False(WebPasswordPolicy.EsValida("panelsuper", "panelsuper", out var error));
        Assert.Contains("identica al usuario", error);
    }

    [Fact]
    public void PasswordWeb_Distinta_Del_Usuario_Del_Panel_EsValida()
    {
        // La regla de "identica al usuario" compara solo con el usuario del PANEL
        // (admin), nunca con el de Firebird. Una clave de >= 8 chars distinta de
        // 'admin' es valida aunque coincida con el usuario de Firebird.
        Assert.True(WebPasswordPolicy.EsValida("sySDBAmaster", "admin", out _));
    }

    [Fact]
    public void PasswordWeb_Contiene_ChangeMe_EsInvalida()
    {
        Assert.False(WebPasswordPolicy.EsValida("ClaveCHANGE_ME!", "admin", out var error));
        Assert.Contains("CHANGE_ME", error);
    }

    // ==================================================================
    // CloudflareHostnamePolicy (reglas puras)
    // ==================================================================

    [Fact]
    public void Hostname_Vacio_EsInvalido()
    {
        Assert.False(CloudflareHostnamePolicy.EsFqdnValido("", out var error));
        Assert.Contains("Debes indicar", error);
    }

    [Fact]
    public void Hostname_Placeholder_Ejemplo_EsInvalido()
    {
        Assert.False(CloudflareHostnamePolicy.EsFqdnValido("sync.ejemplo.com", out var error));
        Assert.Contains("placeholder", error);
    }

    [Fact]
    public void Hostname_Es_IP_EsInvalido()
    {
        Assert.False(CloudflareHostnamePolicy.EsFqdnValido("203.0.113.10", out var error));
        Assert.Contains("IP", error);
    }

    [Fact]
    public void Hostname_Es_Localhost_EsInvalido()
    {
        Assert.False(CloudflareHostnamePolicy.EsFqdnValido("localhost", out _));
    }

    [Theory]
    [InlineData("sin-extension")]
    [InlineData("mal host.com")]
    [InlineData("-mal.com")]
    public void Hostname_FormatoInvalido_EsInvalido(string host)
    {
        Assert.False(CloudflareHostnamePolicy.EsFqdnValido(host, out _));
    }

    [Theory]
    [InlineData("sync.cliente.com")]
    [InlineData("https://sync.cliente.com/")]
    [InlineData("sync.cliente.com:8443")]
    public void Hostname_Valido_EsValido(string host)
    {
        var sanitizado = CloudflareHostnamePolicy.Sanitizar(host);
        Assert.True(CloudflareHostnamePolicy.EsFqdnValido(sanitizado, out _));
    }

    [Fact]
    public void Sanitizar_Vacio_NoDevuelvePlaceholder()
    {
        Assert.Equal("", CloudflareHostnamePolicy.Sanitizar(null));
        Assert.Equal("", CloudflareHostnamePolicy.Sanitizar("   "));
    }

    [Fact]
    public void Sanitizar_Quita_Protocolo_Puerto_Y_Ruta()
    {
        Assert.Equal("sync.cliente.com", CloudflareHostnamePolicy.Sanitizar("https://sync.cliente.com:8443/ruta"));
    }

    // ==================================================================
    // Instalar: validacion previa SIN efectos secundarios
    // ==================================================================

    private static (string src, string raiz) CrearDirectoriosTemporales()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "rutx_inst_test_" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(baseDir, "src");
        var raiz = Path.Combine(baseDir, "raiz");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(raiz);
        File.WriteAllText(Path.Combine(src, "Rutx.Sincronizador.exe"), "fake");
        File.WriteAllText(Path.Combine(src, "appsettings.json"), "{}");
        return (src, raiz);
    }

    [Fact]
    public void Instalar_AccesoRemoto_HostnameVacio_Lanza_AntesDeEfectos()
    {
        var (src, raiz) = CrearDirectoriosTemporales();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                InstalacionHelper.Instalar(raiz, "C:\\db\\base.fdb", "SYSDBA", "pw", src,
                    mobileRemoteAccess: true, webPassword: "Clave8x!", publicHostname: ""));
            Assert.Contains("Debes indicar", ex.Message);

            // Sin efectos secundarios: la raiz no debe contener nada copiado
            Assert.Empty(Directory.GetFiles(raiz));
            Assert.False(File.Exists(Path.Combine(raiz, "instalacion.json")));
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(raiz)!);
        }
    }

    [Fact]
    public void Instalar_AccesoRemoto_HostnamePlaceholder_Lanza()
    {
        var (src, raiz) = CrearDirectoriosTemporales();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                InstalacionHelper.Instalar(raiz, "C:\\db\\base.fdb", "SYSDBA", "pw", src,
                    mobileRemoteAccess: true, webPassword: "Clave8x!", publicHostname: "sync.ejemplo.com"));
            Assert.Contains("placeholder", ex.Message);
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(raiz)!);
        }
    }

    [Fact]
    public void Instalar_PasswordWeb_Debil_Lanza_AntesDeEfectos()
    {
        var (src, raiz) = CrearDirectoriosTemporales();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                InstalacionHelper.Instalar(raiz, "C:\\db\\base.fdb", "SYSDBA", "pw", src,
                    mobileRemoteAccess: false, webPassword: "Corta7!"));
            Assert.Contains("8 caracteres", ex.Message);
            Assert.Empty(Directory.GetFiles(raiz));
        }
        finally
        {
            TryCleanup(Path.GetDirectoryName(raiz)!);
        }
    }

    // ==================================================================
    // Generacion de appsettings (pura, con directorio temporal)
    // ==================================================================

    [Fact]
    public void AppSettings_Con_AccesoRemoto_Genera_Configuracion_Correcta()
    {
        var obj = new JsonObject();
        InstalacionHelper.ConstruirAppSettingsJson(obj,
            "C:\\db\\base.fdb", "SYSDBA", "clave-fb", mobileRemoteAccess: true, publicHostname: "sync.cliente.com");

        Assert.True((bool)obj["Network"]!["ExternalApiEnabled"]!);
        Assert.Equal(5048, (int)obj["Network"]!["ExternalPort"]!);
        Assert.Equal("ReverseProxy", (string)obj["Network"]!["ExternalApiMode"]!);
        Assert.Equal("sync.cliente.com;localhost;127.0.0.1", (string)obj["AllowedHosts"]!);
        Assert.Equal("sync.cliente.com", (string)obj["Cloudflare"]!["PublicHostname"]!);

        // La contraseña del panel NUNCA va en appsettings.json
        var webAuth = obj["WebAuth"] as JsonObject;
        Assert.NotNull(webAuth);
        Assert.Equal("admin", (string)webAuth["AdminUsername"]!);
        Assert.Null(webAuth["AdminPassword"]);

        Assert.NotNull(obj["Jwt"]!["Key"]);
    }

    [Fact]
    public void AppSettings_Sin_AccesoRemoto_No_Deja_Network_Ni_Cloudflare()
    {
        var obj = new JsonObject();
        InstalacionHelper.ConstruirAppSettingsJson(obj,
            "C:\\db\\base.fdb", "SYSDBA", "clave-fb", mobileRemoteAccess: false, publicHostname: "sync.cliente.com");

        Assert.Null(obj["Network"]);
        Assert.Null(obj["Cloudflare"]);
        Assert.Equal("*", (string)obj["AllowedHosts"]!);
    }

    // ==================================================================
    // Helpers locales
    // ==================================================================

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // best effort en tests
        }
    }
}
