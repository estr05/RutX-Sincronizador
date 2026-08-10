using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Compartidos;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class ClientesController : ControllerBase
{
    private readonly IClienteService _clienteService;

    public ClientesController(IClienteService clienteService)
    {
        _clienteService = clienteService;
    }

    [HttpPost]
    public async Task<IActionResult> CrearCliente([FromBody] ClienteCreateDto clienteDto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            int nuevoId = await _clienteService.CrearClienteAsync(clienteDto);
            return Ok(new { 
                message = "Cliente creado exitosamente en Microsip", 
                clienteId = nuevoId 
            });
        }
        catch (Exception ex)
        {
            // En un entorno real se usaría ILogger para registrar el error
            return StatusCode(500, new { message = "Error interno sincronizando cliente con Firebird", error = ex.Message });
        }
    }
}
