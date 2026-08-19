using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Middleware;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Aislamiento del manejo de errores web v2 (Sprint 4 · Bloque 0):
///  - /api/v2/web/* responde el envelope canónico { code, message, errors, trace_id }.
///  - /api/v1/* y /api/v2/admin/* conservan EXACTAMENTE el formato legado
///    { mensaje, detalle, categoria, reintentable } (no se rompe móvil ni admin).
///  - El trace_id propagado por WebTraceIdMiddleware llega al envelope web.
/// </summary>
public class WebErrorEnvelopeTests
{
    private static async Task<(int Status, string Body)> InvocarConRutaAsync(string path, string? xTraceId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (xTraceId != null)
            context.Request.Headers["X-Trace-Id"] = xTraceId;

        // Pipeline real: WebTraceIdMiddleware → ErrorHandlingMiddleware.
        var trace = new WebTraceIdMiddleware(ctx => Task.CompletedTask);
        await trace.InvokeAsync(context);

        var errores = new ErrorHandlingMiddleware(_ => throw new ArgumentException("Dato invalido"), NullLogger<ErrorHandlingMiddleware>.Instance);
        context.Response.Body = new MemoryStream();
        await errores.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task RutaWebV2_DevuelveEnvelopeCanonicoSinFormatoLegado()
    {
        var (status, body) = await InvocarConRutaAsync("/api/v2/web/customers");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("\"code\":\"BAD_REQUEST\"", body);
        Assert.Contains("\"message\":\"Solicitud inv", body); // JsonSerializer escapa tildes (\u00E1)
        Assert.Contains("\"errors\":null", body);
        Assert.Contains("\"trace_id\"", body);

        // El formato legado NO debe filtrarse a la frontera web.
        Assert.DoesNotContain("\"mensaje\"", body);
        Assert.DoesNotContain("\"detalle\"", body);
        Assert.DoesNotContain("\"categoria\"", body);
        Assert.DoesNotContain("\"reintentable\"", body);
        // Ni detalles internos de la excepción.
        Assert.DoesNotContain("Dato invalido", body);
    }

    [Fact]
    public async Task RutaMovilV1_ConservaFormatoLegado()
    {
        var (status, body) = await InvocarConRutaAsync("/api/v1/sync/morning");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("\"mensaje\":\"Solicitud inv", body);
        Assert.Contains("\"detalle\":\"Dato invalido\"", body);
        Assert.Contains("\"categoria\":\"desconocido\"", body);
        Assert.Contains("\"reintentable\":false", body);
        Assert.DoesNotContain("\"code\"", body);
    }

    [Fact]
    public async Task RutaAdminLocal_ConservaFormatoLegado()
    {
        var (status, body) = await InvocarConRutaAsync("/api/v2/admin/vendedores");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("\"mensaje\":\"Solicitud inv", body);
        Assert.Contains("\"detalle\":\"Dato invalido\"", body);
        Assert.Contains("\"categoria\":\"desconocido\"", body);
        Assert.Contains("\"reintentable\":false", body);
    }

    [Fact]
    public async Task RutaWebV2_ReusaTraceIdPropagado()
    {
        var (_, body) = await InvocarConRutaAsync("/api/v2/web/notifications", xTraceId: "01JTRAZA1234567890");

        Assert.Contains("\"trace_id\":\"01JTRAZA1234567890\"", body);
    }

    [Fact]
    public async Task SqlError_EnRutaWebV2_UsaCodeYStatusDeClasificacion()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v2/web/customers";
        context.Response.Body = new MemoryStream();
        context.Items["trace_id"] = "01JTRAZA";

        var errores = new ErrorHandlingMiddleware(_ =>
            throw new Exception("violation of FOREIGN KEY constraint \"X\" on table \"Y\""),
            NullLogger<ErrorHandlingMiddleware>.Instance);
        await errores.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        Assert.Contains("\"code\":\"CONFLICT\"", body);
        Assert.Contains("\"trace_id\":\"01JTRAZA\"", body);
        Assert.DoesNotContain("violation of FOREIGN KEY", body);
    }
}