using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Rutx.Sincronizador.Models;

public class InventarioRestanteDto
{
    [Required(ErrorMessage = "El ID del artículo es obligatorio.")]
    public int ArticuloId { get; set; }

    [Required(ErrorMessage = "Las unidades restantes son obligatorias.")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Las unidades restantes deben ser mayores a cero.")]
    public decimal UnidadesRestantes { get; set; }
}

public class MermaDto
{
    [Required(ErrorMessage = "El ID del artículo es obligatorio.")]
    public int ArticuloId { get; set; }

    [Required(ErrorMessage = "Las unidades de merma son obligatorias.")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Las unidades mermadas deben ser mayores a cero.")]
    public decimal Unidades { get; set; }

    [Required(ErrorMessage = "El motivo de la merma es obligatorio.")]
    [MaxLength(200, ErrorMessage = "El motivo no puede exceder los 200 caracteres.")]
    public string Motivo { get; set; } = string.Empty;
}

public class DevolucionDto
{
    [Required(ErrorMessage = "El ID del cliente es obligatorio.")]
    public int ClienteId { get; set; }

    [Required(ErrorMessage = "El ID del artículo es obligatorio.")]
    public int ArticuloId { get; set; }

    [Required(ErrorMessage = "Las unidades devueltas son obligatorias.")]
    [Range(0.00001, double.MaxValue, ErrorMessage = "Las unidades devueltas deben ser mayores a cero.")]
    public decimal Unidades { get; set; }

    [Required(ErrorMessage = "El motivo de la devolución es obligatorio.")]
    [MaxLength(200, ErrorMessage = "El motivo no puede exceder los 200 caracteres.")]
    public string Motivo { get; set; } = string.Empty;
}

public class ClosingDto
{
    [Required(ErrorMessage = "El ID del vendedor es obligatorio.")]
    public int VendedorId { get; set; }

    [Required(ErrorMessage = "El nombre de la ruta activa es obligatorio.")]
    [MaxLength(100, ErrorMessage = "El nombre de la ruta no puede exceder los 100 caracteres.")]
    public string RutaActiva { get; set; } = string.Empty;

    [Required(ErrorMessage = "La fecha y hora del cierre son obligatorias.")]
    public DateTime FechaHora { get; set; }

    public List<InventarioRestanteDto> InventarioFinal { get; set; } = new();

    public List<MermaDto> Mermas { get; set; } = new();

    public List<DevolucionDto> Devoluciones { get; set; } = new();
}
