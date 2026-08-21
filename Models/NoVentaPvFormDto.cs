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
    [FromForm(Name = "payload")]
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>Archivo de la fotografia (campo `foto` del multipart).</summary>
    [FromForm(Name = "foto")]
    public IFormFile? Foto { get; set; }
}
