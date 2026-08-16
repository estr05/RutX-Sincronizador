using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;
using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Middleware;
using Rutx.Sincronizador.Services;
using Rutx.Sincronizador.Services.Web;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
// ----------------------------------------------------------------
// ContentRoot fijo: siempre el directorio del exe.
// Cuando se ejecuta como servicio (SCM) o tarea programada, el
// WorkingDirectory no es la carpeta del exe; fijarlo aqui garantiza
// que appsettings.json, wwwroot y Data se resuelvan correctamente.
// ----------------------------------------------------------------
var exeDir = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(exeDir);

// ----------------------------------------------------------------
// Single-instance lock portable (archivo bloqueado en %TEMP%).
// Funciona en Windows y Linux, a diferencia de Mutex (solo Win).
// Si el archivo queda huerfano (cierre abrupto/crash), se detecta y
// se elimina automaticamente: no hace falta borrarlo a mano.
// ----------------------------------------------------------------
string lockFilePath = Path.Combine(Path.GetTempPath(), "RutxSincronizador_5047.lock");
FileStream? lockFile = null;
for (int intento = 0; intento < 2; intento++)
{
    try
    {
        lockFile = new FileStream(lockFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        break;
    }
    catch (IOException)
    {
        if (intento == 0)
        {
            // Si nadie lo tiene abierto (FileShare.None), es un candado
            // huerfano de un proceso que ya no existe: se puede borrar.
            try
            {
                using (new FileStream(lockFilePath, FileMode.Open, FileAccess.Write, FileShare.None)) { }
                File.Delete(lockFilePath);
                continue;
            }
            catch (IOException)
            {
                // Otro proceso lo tiene abierto -> instancia real en ejecucion.
            }
        }
        Console.Error.WriteLine("[ERROR] Ya hay una instancia del Sincronizador ejecutandose.");
        Console.Error.WriteLine("       Si es el servicio, usa el launcher para gestionarlo.");
        return;
    }
}
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    lockFile?.Dispose();
    File.Delete(lockFilePath);
};
Console.CancelKeyPress += (_, _) =>
{
    lockFile?.Dispose();
    File.Delete(lockFilePath);
};
// ----------------------------------------------------------------

// ----------------------------------------------------------------
// Logging a archivo: indispensable cuando corre como servicio
// (no hay consola). Logs en Logs/sincronizador.log (rotacion diaria).
// ----------------------------------------------------------------
var logsDir = Path.Combine(exeDir, "Logs");
Directory.CreateDirectory(logsDir);

var builder = WebApplication.CreateBuilder(args);

// ContentRoot = directorio del exe (no depende de WorkingDirectory)
builder.Environment.ContentRootPath = exeDir;

// Windows Service: detecta automaticamente si corre como servicio.
// En modo consola (desarrollo) funciona igual, sin cambios.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "RutxSincronizador";
});

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:5047");

// Logging a archivo (ademas de consola en dev)
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// EventLog solo existe en Windows; en Linux no hay y lanzaria
// PlatformNotSupportedException al arrancar. El guard condicional
// se valida con OperatingSystem.IsWindows() (por eso el pragma).
if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "RutxSincronizador";
        settings.LogName = "Sincronizador";
    });
#pragma warning restore CA1416
}
builder.Logging.AddProvider(new FileLoggerProvider(logsDir));

// --- Fallback dinámico para la ruta de la base de datos Firebird ---
var connectionString = builder.Configuration.GetConnectionString("FirebirdConnection");
var myDbPath = @"C:\FirebirdData\CHOCOLATES.fdb";

// Fallback 1: ruta de desarrollo Microsip → FirebirdData
var devDbPath = @"C:\Microsip\CHOCOLATES.fdb";
if (connectionString != null && connectionString.Contains(devDbPath) && !System.IO.File.Exists(devDbPath))
{
    if (System.IO.File.Exists(myDbPath))
    {
        connectionString = connectionString.Replace(devDbPath, myDbPath);
        builder.Configuration["ConnectionStrings:FirebirdConnection"] = connectionString;
    }
}

// Fallback 2: ruta Downloads → FirebirdData
var downloadsDbPath = @"C:\Users\ADMIN\Downloads\CHOCOLATES.fdb";
if (connectionString != null && connectionString.Contains(downloadsDbPath) && !System.IO.File.Exists(downloadsDbPath))
{
    if (System.IO.File.Exists(myDbPath))
    {
        connectionString = connectionString.Replace(downloadsDbPath, myDbPath);
        builder.Configuration["ConnectionStrings:FirebirdConnection"] = connectionString;
    }
}
// -------------------------------------------------------------------

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        opts.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();

// Servicio de autenticacion nativa contra Firebird (sin AGENTES)
builder.Services.AddScoped<IFirebirdAuthService, FirebirdAuthService>();

// Servicio de Clientes (Hannia)
builder.Services.AddScoped<IClienteService, ClienteService>();

// Servicio de Ventas PV (Punto de Venta)
builder.Services.AddScoped<IVentaServicePv, VentaServicePv>();

// Folios seguros (reserva atomica + candado por caja + reintentos)
builder.Services.AddSingleton<IFolioLockService, FolioLockService>();
builder.Services.AddSingleton<IFirebirdRetryPolicy, FirebirdRetryPolicy>();
builder.Services.AddScoped<IFolioService, FolioService>();

// Servicio de Rutas (Ventas en Ruta)
builder.Services.AddScoped<IRouteService, RouteService>();
builder.Services.AddScoped<IDebugRouteService>(sp => (IDebugRouteService)sp.GetRequiredService<IRouteService>());

// Reconciliacion de movimientos de inventario (TRANSFER / SEED / ANOMALIA)
builder.Services.AddScoped<IInventarioReconciliacionService, InventarioReconciliacionService>();
builder.Services.AddHostedService<ReconciliacionInventarioBackgroundService>();

// Almacenamiento de fotos de no-ventas (carpeta configurable Storage:FotosPath)
builder.Services.AddSingleton<IFotoStorageService, FotoStorageService>();

// Servicio de Cobranza (Pago de creditos)
builder.Services.AddScoped<ICobranzaService, CobranzaService>();

// Servicio de Credito (Consulta de saldos pendientes)
builder.Services.AddScoped<ICreditoService, CreditoService>();

// Servicio de resolucion dinamica de Foreign Keys
builder.Services.AddScoped<IFkResolverService, FkResolverService>();

// Auditoria de compatibilidad BD Microsip <-> sincronizador (wizard + panel web)
builder.Services.AddScoped<IAuditoriaCompatibilidadService, AuditoriaCompatibilidadService>();

// Cola Offline (Eduardo)
string sqlitePath = builder.Configuration.GetValue<string>("ColaOffline:RutaSqlite") ?? "Data/cola_offline.db";

// La ruta de la cola SQLite es relativa al directorio de trabajo. Al ser
// lanzado por el launcher (WorkingDirectory = carpeta del exe), la carpeta
// Data/ puede no existir ahi y SQLite falla con 'Error 14: unable to open
// database file'. Se resuelve contra el ContentRoot y se crea la carpeta.
if (!Path.IsPathRooted(sqlitePath))
    sqlitePath = Path.Combine(builder.Environment.ContentRootPath, sqlitePath);
var sqliteDir = Path.GetDirectoryName(sqlitePath);
if (!string.IsNullOrWhiteSpace(sqliteDir))
    Directory.CreateDirectory(sqliteDir);
string sqliteConnectionString = $"Data Source={sqlitePath}";

builder.Services.AddSingleton<IColaOfflineRepository>(new ColaOfflineRepository(sqliteConnectionString));
builder.Services.AddScoped<IColaOfflineService, ColaOfflineService>();
builder.Services.AddHostedService<BackgroundSyncService>();

// Servicios v2 web (contrato v2 §8): flujo Controller Web → Interface Web → Service Web.
builder.Services.AddScoped<IDashboardWebService, DashboardWebService>();
builder.Services.AddScoped<IReportsWebService, ReportsWebService>();
builder.Services.AddScoped<IRouteMonitoringWebService, RouteMonitoringWebService>();
builder.Services.AddScoped<IWebAuthService, WebAuthService>();
builder.Services.AddScoped<ICustomerWebService, CustomerWebService>();
builder.Services.AddScoped<IInventoryWebService, InventoryWebService>();
builder.Services.AddScoped<INotificationWebService, NotificationWebService>();

// BD complementaria web (SQLite): esquema gestionado por migraciones
// versionadas (WebSqliteMigrator + schema_version) y credenciales de
// administrador inicial SOLO desde configuración externa (nunca versionadas).
string webSqlitePath = builder.Configuration.GetValue<string>("WebSqlite:Ruta")
    ?? "Data/web.db";
if (!Path.IsPathRooted(webSqlitePath))
    webSqlitePath = Path.Combine(builder.Environment.ContentRootPath, webSqlitePath);
var webSqliteDir = Path.GetDirectoryName(webSqlitePath);
if (!string.IsNullOrWhiteSpace(webSqliteDir))
    Directory.CreateDirectory(webSqliteDir);
string webSqliteConnectionString = $"Data Source={webSqlitePath}";

builder.Services.AddSingleton<IWebSqliteStore>(sp =>
    new WebSqliteStore(webSqliteConnectionString, sp.GetRequiredService<ILogger<WebSqliteStore>>()));

// Configuración JWT (Mauricio)
var key = builder.Configuration["Jwt:Key"];
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key!))
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Portal web v2: cada permiso del contrato (§8) es una política.
    // El handler exige scope=web + el permiso en los claims del JWT.
    foreach (var permiso in Rutx.Sincronizador.Services.Web.WebRoleCatalog
                 .PermisosPorRol.Values
                 .SelectMany(p => p)
                 .Distinct(StringComparer.Ordinal))
    {
        options.AddPolicy($"web.{permiso}",
            policy => policy.Requirements.Add(new Rutx.Sincronizador.Security.Web.WebPermissionRequirement(permiso)));
    }

    // Endpoints de identidad: cualquier token del portal (scope=web).
    options.AddPolicy("web.any",
        policy => policy.Requirements.Add(new Rutx.Sincronizador.Security.Web.WebPermissionRequirement(string.Empty)));
});

// Handler de autorización por permiso del portal.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, Rutx.Sincronizador.Security.Web.WebPermissionHandler>();

// Rate limit del login del portal: 5 intentos por IP por minuto (contrato v2 §5).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("web-login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonimo",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

var app = builder.Build();

// Frontera web v2: trace_id se propaga ANTES del manejo de errores para que
// el envelope de error web lo incluya. Móvil y administrador local conservan
// su formato legado (ErrorHandlingMiddleware ramifica por prefijo /api/v2/web).
app.UseMiddleware<WebTraceIdMiddleware>();
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseStaticFiles(); // Panel de administracion (wwwroot)
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Esquema web (migraciones versionadas) + administrador inicial controlado.
// El arranque no se bloquea si la BD complementaria no está disponible:
// los endpoints web responderán 503 hasta que se resuelva.
try
{
    var webStore = app.Services.GetRequiredService<IWebSqliteStore>();
    var version = await webStore.EnsureSchemaAsync();
    await webStore.EnsureAdminSeedAsync(app.Configuration);
    app.Logger.LogInformation("BD complementaria web lista (esquema v{Version}).", version);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "No se pudo preparar la BD complementaria web. Los endpoints /api/v2/web quedan indisponibles hasta resolverlo.");
}

// Health check: permite que la app móvil detecte si el servidor es accesible

// Panel de administracion web (config bajo demanda).
app.MapGet("/admin", () => Results.File(
    Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin.html"),
    "text/html"));
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();
