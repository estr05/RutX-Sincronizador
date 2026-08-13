using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Rutx.Sincronizador.Models;

/// <summary>
/// DTO de la NO VENTA cuando llega como multipart/form-data desde la app
/// (los campos van en snake_case, igual que el JSON anterior, y la foto se
/// adjunta como archivo `foto`).
///
/// Se mapea a <see cref="NoVentaPvCreateDto"/> en el controlador; la foto se
/// guarda en disco y su referencia (nombre de archivo) viaja como FotoPath
/// para que el servicio la deje en DOCTOS_PV.DESCRIPCION (segmento FOTO:).
/// </summary>
public class NoVentaPvFormDto
{
    [Required]
    [FromForm(Name = "venta_movil_id")]
    public string VentaMovilId { get; set; } = string.Empty;

    [Required]
    [FromForm(Name = "vendedor_id")]
    public int VendedorId { get; set; }

    [Required]
    [FromForm(Name = "cliente_id")]
    public int ClienteId { get; set; }

    [Required]
    [FromForm(Name = "fecha_hora")]
    public DateTime FechaHora { get; set; }

    [FromForm(Name = "caja_id")]
    public int? CajaId { get; set; }

    [FromForm(Name = "cajero_id")]
    public int? CajeroId { get; set; }

    [FromForm(Name = "usuario_creador")]
    public string? UsuarioCreador { get; set; }

    [Required]
    [FromForm(Name = "causa_id")]
    public int CausaId { get; set; }

    [Required]
    [FromForm(Name = "causa_desc")]
    public string CausaDesc { get; set; } = string.Empty;

    [FromForm(Name = "comentario")]
    public string? Comentario { get; set; }

    /// <summary>Archivo de la fotografia (campo `foto` del multipart).</summary>
    public IFormFile? Foto { get; set; }
}
