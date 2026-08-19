using Rutx.Sincronizador.Security;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

public class WebPasswordHasherTests
{
    [Fact]
    public void Hash_GeneraFormatoVersionadoConIteraciones()
    {
        var hash = WebPasswordHasher.Hash("Clave!Segura123");

        var partes = hash.Split('$');
        Assert.Equal(4, partes.Length);
        Assert.Equal("pbkdf2-sha256", partes[0]);
        Assert.Equal(WebPasswordHasher.IteracionesRecomendadas.ToString(), partes[1]);
        Assert.NotEmpty(partes[2]);
        Assert.NotEmpty(partes[3]);
    }

    [Fact]
    public void Verify_ContrasenaCorrecta_DevuelveTrue()
    {
        var hash = WebPasswordHasher.Hash("Clave!Segura123");
        Assert.True(WebPasswordHasher.Verify("Clave!Segura123", hash));
    }

    [Fact]
    public void Verify_ContrasenaIncorrecta_DevuelveFalse()
    {
        var hash = WebPasswordHasher.Hash("Clave!Segura123");
        Assert.False(WebPasswordHasher.Verify("otra-clave", hash));
    }

    [Fact]
    public void Hash_DosLlamadas_SalesDistintas()
    {
        var hash1 = WebPasswordHasher.Hash("MismaClave");
        var hash2 = WebPasswordHasher.Hash("MismaClave");
        Assert.NotEqual(hash1, hash2);
        Assert.True(WebPasswordHasher.Verify("MismaClave", hash1));
        Assert.True(WebPasswordHasher.Verify("MismaClave", hash2));
    }

    [Fact]
    public void Hash_NoContieneLaContrasenaEnClaro()
    {
        var hash = WebPasswordHasher.Hash("ContrasenaSecreta2026!");
        Assert.DoesNotContain("ContrasenaSecreta2026", hash);
    }

    [Fact]
    public void Verify_FormatoInvalido_DevuelveFalseSinExcepcion()
    {
        Assert.False(WebPasswordHasher.Verify("x", "basura-sin-formato"));
        Assert.False(WebPasswordHasher.Verify("x", "pbkdf2-sha256$abc$def$ghi"));
        Assert.False(WebPasswordHasher.Verify("x", ""));
    }

    [Fact]
    public void NecesitaRehash_IteracionesViejas_DevuelveTrue()
    {
        // Hash "viejo" construido con 100.000 iteraciones (formato legítimo anterior al estándar).
        var sal = Convert.ToBase64String(new byte[16]);
        var hashViejo = $"pbkdf2-sha256$100000${sal}${Convert.ToBase64String(new byte[32])}";
        Assert.True(WebPasswordHasher.NecesitaRehash(hashViejo));

        var hashVigente = WebPasswordHasher.Hash("Clave");
        Assert.False(WebPasswordHasher.NecesitaRehash(hashVigente));
    }

    [Fact]
    public void Hash_IteracionesMenoresAlMinimo_LanzaExcepcion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebPasswordHasher.Hash("Clave", iteraciones: 1_000));
    }
}