namespace Rutx.Sincronizador.Models;

public class MensajeDto
{
    public int VendedorId { get; set; }
    public string Contenido { get; set; } = string.Empty;
    public DateTime FechaEnvio { get; set; } = DateTime.UtcNow;
}