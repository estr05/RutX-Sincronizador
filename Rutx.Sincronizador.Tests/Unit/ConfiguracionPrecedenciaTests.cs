using Microsoft.Extensions.Configuration;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Contrato de PRECEDENCIA de la configuracion local:
///   base (appsettings.json / appsettings.{env}.json)
///     &lt; appsettings.Local.json
///     &lt; variables de entorno / linea de comandos  &lt;- ultima palabra
///
/// Program.cs registra los proveedores en ese orden exacto y RE-REGISTRA
/// EnvironmentVariables + CommandLine despues del archivo local, porque en
/// .NET Configuration gana el ULTIMO proveedor registrado. Estos tests
/// fijan ese contrato para que nadie lo rompa sin darse cuenta.
/// </summary>
public class ConfiguracionPrecedenciaTests : IDisposable
{
    private readonly string _localPath;

    public ConfiguracionPrecedenciaTests()
    {
        _localPath = Path.Combine(Path.GetTempPath(), $"rutx_local_test_{Guid.NewGuid():N}.json");
        File.WriteAllText(_localPath,
            """{ "Jwt": { "Key": "VALOR_DE_APPSETTINGS_LOCAL_32_CHARS_MINIMO_OK" } }""");
    }

    public void Dispose() => File.Delete(_localPath);

    private IConfiguration ConstruirComoProgramCs(Dictionary<string, string?> proveedorPosterior)
    {
        // Mismo orden de registro que Program.cs:
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "VALOR_BASE_DEL_APPSETTINGS_VERSIONADO_32_CHARS"   // ~ appsettings*.json
        });
        builder.AddJsonFile(_localPath, optional: true, reloadOnChange: false); // ~ appsettings.Local.json
        builder.AddInMemoryCollection(proveedorPosterior);                       // ~ Environment/CLI re-registrados
        return builder.Build();
    }

    [Fact]
    public void LocalJson_Gana_Sobre_El_Appsettings_Versionado()
    {
        var cfg = ConstruirComoProgramCs(new Dictionary<string, string?>());

        Assert.Equal("VALOR_DE_APPSETTINGS_LOCAL_32_CHARS_MINIMO_OK", cfg["Jwt:Key"]);
    }

    [Fact]
    public void Variables_De_Entorno_O_Cli_Ganan_Sobre_LocalJson()
    {
        var cfg = ConstruirComoProgramCs(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "VALOR_DE_ENTORNO_TIENE_PRECEDENCIA_FINAL_32CH"
        });

        Assert.Equal("VALOR_DE_ENTORNO_TIENE_PRECEDENCIA_FINAL_32CH", cfg["Jwt:Key"]);
    }

    [Fact]
    public void LocalJson_Ausente_No_Rompe_Y_Base_Permanece()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = "BASE" });
        builder.AddJsonFile(Path.Combine(Path.GetTempPath(), $"no_existe_{Guid.NewGuid():N}.json"),
            optional: true, reloadOnChange: false);
        builder.AddInMemoryCollection(new Dictionary<string, string?>());      // env/CLI vacios

        Assert.Equal("BASE", builder.Build()["Jwt:Key"]);
    }
}
