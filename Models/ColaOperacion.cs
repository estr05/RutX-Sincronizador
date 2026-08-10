namespace Rutx.Sincronizador.Models;

public class ColaOperacion
{
    public int Id { get; set; }
    public string OperacionId { get; set; } = Guid.NewGuid().ToString();
    public TipoOperacion TipoOperacion { get; set; }
    public string Payload { get; set; } = string.Empty;
    public EstadoOperacion Estado { get; set; } = EstadoOperacion.PENDIENTE;
    public int Intentos { get; set; } = 0;
    public int MaxIntentos { get; set; } = 5;
    public DateTime SiguienteReintento { get; set; } = DateTime.UtcNow;
    public string? ErrorUltimoIntento { get; set; }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public DateTime FechaModificacion { get; set; } = DateTime.UtcNow;
}
