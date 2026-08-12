using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Middleware;
using Rutx.Sincronizador.Models;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

public class ErrorHandlingMiddlewareTest
{
    private static async Task<string> InvocarAsync(Func<Task> pipelineAction)
    {
        var middleware = new ErrorHandlingMiddleware(_ => pipelineAction(), NullLogger<ErrorHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
    }

    [Fact]
    public async Task ViolacionLlaveForanea_Devuelve409ConCategoria()
    {
        var body = await InvocarAsync(() =>
            throw new Exception("violation of FOREIGN KEY constraint \"ARTS_A_DOCTOS_PV_DET\" on table \"DOCTOS_PV_DET\""));

        Assert.Contains("\"categoria\":\"ViolacionLlaveForanea\"", body);
        Assert.Contains("\"reintentable\":false", body);
        Assert.Contains("llave foranea", body);
        Assert.Contains("ARTS_A_DOCTOS_PV_DET", body);
    }

    [Fact]
    public async Task OdsNoSoportada_Devuelve500ConMensajeAccionable()
    {
        var body = await InvocarAsync(() =>
            throw new Exception("unsupported on-disk structure for file C:\\X.FDB; found 13.1, support 12.2"));

        Assert.Contains("\"categoria\":\"OdsNoSoportada\"", body);
        Assert.Contains("Firebird", body);
    }

    [Fact]
    public async Task ErrorConexion_Devuelve503Reintentable()
    {
        var body = await InvocarAsync(() =>
            throw new Exception("No connection could be made because the target machine actively refused it. 127.0.0.1:3050"));

        Assert.Contains("\"reintentable\":true", body);
        Assert.Contains("No se pudo conectar", body);
    }

    [Fact]
    public async Task DeadlockTransientException_ConInnerFb_Devuelve503Reintentable()
    {
        // FirebirdRetryPolicy lanza DeadlockTransientException con inner FbException (lock conflict).
        var body = await InvocarAsync(() =>
            throw new DeadlockTransientException("deadlock",
                new Exception("lock conflict on no wait transaction")));

        Assert.Contains("\"categoria\":\"BloqueoODeadlock\"", body);
        Assert.Contains("\"reintentable\":true", body);
        Assert.Contains("bloqueo", body);
    }

    [Fact]
    public async Task ExcepcionDeNegocio_ConservaElComportamientoAnterior()
    {
        var body = await InvocarAsync(() => throw new ArgumentException("Dato invalido"));

        Assert.Contains("Solicitud inv", body); // la tilde se serializa escapada (\u00E1)
        Assert.Contains("Dato invalido", body);
        Assert.Contains("\"categoria\":\"desconocido\"", body);
    }
}
