namespace Rutx.Sincronizador.Models;

/// <summary>
/// Identidad resuelta en el login del usuario de RUTX.
///
/// El login valida las credenciales REALES del usuario de Firebird
/// (usuario + password de Microsip) y con esa identidad resuelve:
///   - VendedorId  : VENDEDORES.NOMBRE = usuario (convencion de RUTX)
///   - CajeroId    : CAJEROS.USUARIO = usuario
///   - CajaId      : CAJAS_CAJEROS (TIPO_ACCESO 'A' -> 'O') o evidencia
///                   (MOVTOS_CAJAS.USUARIO_CREADOR / DOCTOS_PV.USUARIO_CREADOR)
///   - AlmacenId   : CAJAS.ALMACEN_ID de la caja resuelta
///   - SucursalId  : MicrosipSettings:DefaultSucursalId
///
/// Esta informacion viaja en los claims del JWT y se usa en la
/// sincronizacion matutina y en el registro de ventas PV.
/// </summary>
public class UsuarioSesion
{
    public int VendedorId { get; set; }
    public string VendedorNombre { get; set; } = string.Empty;
    public int CajeroId { get; set; }
    public int CajaId { get; set; }
    public int AlmacenId { get; set; }
    public int SucursalId { get; set; }
    public string Usuario { get; set; } = string.Empty;
}
