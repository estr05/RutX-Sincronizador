using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Controllers.Movil;

[Authorize]
[ApiController]
[Route("api/v1/messages")]
public class MensajesController : ControllerBase
{
    private static readonly List<MensajeDto> _mensajes = new();

    [HttpGet]
    public IActionResult ObtenerMensajes([FromQuery] int? vendedorId)
    {
        var mensajes = vendedorId.HasValue
            ? _mensajes.Where(m => m.VendedorId == vendedorId.Value)
            : _mensajes;

        return Ok(mensajes.OrderByDescending(m => m.FechaEnvio));
    }

    [HttpPost]
    public IActionResult EnviarMensaje([FromBody] MensajeDto mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje.Contenido))
            return BadRequest(new { message = "El mensaje no puede estar vacío." });

        _mensajes.Add(mensaje);
        return StatusCode(201, new { message = "Mensaje enviado", confirmado = true });
    }
}