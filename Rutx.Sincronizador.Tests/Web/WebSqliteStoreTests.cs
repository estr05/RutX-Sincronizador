using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Security;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del almacén web: creación de usuarios con hash, búsqueda,
/// zonas por vendedor y, sobre todo, la política del administrador inicial:
/// credenciales SOLO desde configuración externa, hash PBKDF2, rotación
/// obligatoria (must_change_password=1) y arranque bloqueado sin configuración.
/// </summary>
public class WebSqliteStoreTests
{
    private static (WebSqliteStore Store, string Db) CrearStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rutx-web-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "web.db");
        var store = new WebSqliteStore($"Data Source={db}", NullLogger<WebSqliteStore>.Instance);
        return (store, db);
    }

    private static IConfiguration ConfigurarAdmin(string? username, string? password)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WebAuth:AdminUsername"] = username,
            ["WebAuth:AdminPassword"] = password,
        };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public async Task EnsureSchema_CreaEsquemaVersionado()
    {
        var (store, _) = CrearStore();
        var version = await store.EnsureSchemaAsync();
        Assert.Equal(4, version);
        Assert.Equal(4, await store.SchemaVersionAsync());
    }

    [Fact]
    public async Task CreateUser_AlmacenaHashNoContrasena()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();
        var hash = WebPasswordHasher.Hash("Clave!Inicial");

        var usuario = await store.CreateUserAsync("supervisor.centro", "María Hernández", hash, """["supervisor"]""", mustChangePassword: false);

        var leido = await store.FindUserByUsernameAsync("supervisor.centro");
        Assert.NotNull(leido);
        Assert.Equal(usuario.Id, leido!.Id);
        Assert.DoesNotContain("Clave!Inicial", leido.PasswordHash);
        Assert.True(WebPasswordHasher.Verify("Clave!Inicial", leido.PasswordHash));
        Assert.Equal(1, await store.CountUsersAsync());
    }

    [Fact]
    public async Task FindUserByUsername_EsInsensibleAMayusculas()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();
        await store.CreateUserAsync("admin.coyatoc", "Admin", WebPasswordHasher.Hash("Clave!123"), """["administrador"]""", false);

        Assert.NotNull(await store.FindUserByUsernameAsync("ADMIN.COYATOC"));
    }

    [Fact]
    public async Task UsuarioDuplicado_LanzaExcepcion()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();
        await store.CreateUserAsync("dup", "Dup", WebPasswordHasher.Hash("Clave!123"), "[]", false);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.CreateUserAsync("dup", "Dup2", WebPasswordHasher.Hash("Clave!456"), "[]", false));
    }

    [Fact]
    public async Task AdminSeed_SinConfiguracion_NoCreaNingunUsuario()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        var creado = await store.EnsureAdminSeedAsync(ConfigurarAdmin(null, null));

        Assert.False(creado);
        Assert.Equal(0, await store.CountUsersAsync());
    }

    [Fact]
    public async Task AdminSeed_ConConfiguracion_CreaAdminConHashYRotacionObligatoria()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        var creado = await store.EnsureAdminSeedAsync(ConfigurarAdmin("admin", "Contraseña!DeInstalacion42"));

        Assert.True(creado);
        var admin = await store.FindUserByUsernameAsync("admin");
        Assert.NotNull(admin);
        Assert.True(admin!.MustChangePassword, "El primer acceso debe forzar rotación de contraseña.");
        Assert.DoesNotContain("Contraseña!DeInstalacion42", admin.PasswordHash);
        Assert.True(WebPasswordHasher.Verify("Contraseña!DeInstalacion42", admin.PasswordHash));
    }

    [Fact]
    public async Task AdminSeed_DosLlamadas_NoDuplicaUsuario()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();
        var config = ConfigurarAdmin("admin", "Contraseña!DeInstalacion42");

        await store.EnsureAdminSeedAsync(config);
        var segundo = await store.EnsureAdminSeedAsync(config);

        Assert.False(segundo);
        Assert.Equal(1, await store.CountUsersAsync());
    }

    [Fact]
    public async Task ZonasPorVendedor_ResuelveMapeo()
    {
        var (store, db) = CrearStore();
        await store.EnsureSchemaAsync();

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_zones (zone_id, name) VALUES (1, 'Zona Norte'), (2, 'Zona Sur');
            INSERT INTO web_zone_sellers (zone_id, seller_id) VALUES (1, 7), (1, 9), (2, 7);
            """;
        await cmd.ExecuteNonQueryAsync();

        Assert.Equal(new[] { 1, 2 }, (await store.ZonesForSellerAsync(7)).OrderBy(z => z));
        Assert.Equal(new[] { 7, 9 }, (await store.SellersForZoneAsync(1)).OrderBy(s => s));
        Assert.Equal(new[] { 7 }, await store.SellersForZoneAsync(2));
    }

    [Fact]
    public async Task LogAudit_RegistraTrazaCompleta()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        var entrada = await store.LogAuditAsync(1, "admin", "notifications.send", "aviso a ruta 3", "127.0.0.1", "01JTRACE123");

        Assert.True(entrada.Id > 0);
        Assert.Equal("notifications.send", entrada.Action);
        Assert.Equal("01JTRACE123", entrada.TraceId);
        Assert.Equal("127.0.0.1", entrada.IpAddress);
    }
}