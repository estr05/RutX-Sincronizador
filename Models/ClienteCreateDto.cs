using System.ComponentModel.DataAnnotations;

namespace Rutx.Sincronizador.Models;

public class ClienteCreateDto
{
    [Required]
    [MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    public string SujetoIeps { get; set; } = "N";

    public bool DiferirCfdiCobros { get; set; } = false;

    public decimal LimiteCredito { get; set; } = 0.00m;

    // Estos pueden venir nulos de la app móvil. El Sincronizador inyectará los valores por defecto del appsettings.
    public int? MonedaId { get; set; }
    
    public int? CondPagoId { get; set; }
}
