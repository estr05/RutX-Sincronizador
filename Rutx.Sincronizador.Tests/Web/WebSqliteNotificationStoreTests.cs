using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Security;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del almacén web para v002 (zone_ids) y la bandeja de
/// notificaciones: ciclo de vida de la emisión, idempotencia y alcance
/// por zonas del usuario en el servidor.
/// </summary>
public class WebSqliteNotificationStoreTests
{
    private static (WebSqliteStore Store, string Db) CrearStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rutx-web-notif", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "web.db");
        var store = new WebSqliteStore($"Data Source={db}", NullLogger<WebSqliteStore>.Instance);
        return (store, db);
    }

    [Fact]
    public async Task CreateUser_ConZoneIds_SeConservaEnLaLectura()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        await store.CreateUserAsync("supervisor.norte", "Norte", WebPasswordHasher.Hash("Clave!123"), """["supervisor"]""", false, "[3793, 3794]");

        var leido = await store.FindUserByUsernameAsync("supervisor.norte");
        Assert.NotNull(leido);
        Assert.Equal("[3793, 3794]", leido!.ZoneIdsJson);
    }

    [Fact]
    public async Task CreateUser_SinZoneIds_ElDefaultEsSinRestriccion()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        await store.CreateUserAsync("admin.coyatoc", "Admin", WebPasswordHasher.Hash("Clave!123"), """["administrador"]""", false);

        var leido = await store.FindUserByUsernameAsync("admin.coyatoc");
        Assert.Equal("[]", leido!.ZoneIdsJson);
    }

    [Fact]
    public async Task UpdateUserPassword_MarcaPasswordCambiada()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();
        var usuario = await store.CreateUserAsync("rotar", "Rotar", WebPasswordHasher.Hash("Clave!123"), "[]", true);

        await store.UpdateUserPasswordAsync(usuario.Id, WebPasswordHasher.Hash("NuevaClave!1"));

        var leido = await store.FindUserByIdAsync(usuario.Id);
        Assert.NotNull(leido);
        Assert.False(leido!.MustChangePassword);
        Assert.NotNull(leido.PasswordChangedAt);
        Assert.True(WebPasswordHasher.Verify("NuevaClave!1", leido.PasswordHash));
    }

    [Fact]
    public async Task EmisionYCiclodeVida_ListaPorAlcanceDeZonas()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        await store.CreateNotificationAsync("seller", 695, "Reunión", "Viernes 9:00", "normal", 1, "admin.coyatoc", "key-1", "trace-1");
        await store.CreateNotificationAsync("route", 9647, "Ruta 9647", "Aviso", "alta", 1, "admin.coyatoc", "key-2", "trace-1");
        await store.CreateNotificationAsync("zone", 3793, "Zona norte", "Todos", "urgente", 1, "admin.coyatoc", "key-3", "trace-1");

        var (items, total) = await store.ListNotificationsAsync(null, null, new[] { 695 }, new[] { 3793 }, 1, 25);
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);

        var (porTipo, _) = await store.ListNotificationsAsync(null, "zone", null, null, 1, 25);
        Assert.Single(porTipo);
        Assert.Equal(3793, porTipo[0].TargetId);

        var activos = await store.CountActiveNotificationsAsync(null, null);
        Assert.Equal(3, activos);
    }

    [Fact]
    public async Task List_SinRestriccion_DevuelveTodo()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        await store.CreateNotificationAsync("seller", 695, "A", "", "normal", 1, "admin", "k1", "t");
        await store.CreateNotificationAsync("zone", 3795, "B", "", "normal", 1, "admin", "k2", "t");

        var (items, total) = await store.ListNotificationsAsync(null, null, null, null, 1, 25);
        Assert.Equal(2, total);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Idempotencia_ClaveUnicaDevuelveNull_YRepetidaLaEncuentra()
    {
        var (store, _) = CrearStore();
        await store.EnsureSchemaAsync();

        Assert.Null(await store.FindNotificationByIdempotencyAsync("clave-única"));

        await store.CreateNotificationAsync("seller", 695, "A", "", "normal", 1, "admin", "clave-única", "t");

        var encontrada = await store.FindNotificationByIdempotencyAsync("clave-única");
        Assert.NotNull(encontrada);
        Assert.Equal("clave-única", encontrada!.IdempotencyKey);
    }
}