using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Saga idempotente de registro de no-ventas con foto.
///
/// Orquesta: validación → hash → BD/C → staging → Firebird → BD/C → promoción.
/// El controlador delega completamente en esta interfaz; no conoce staging,
/// hashes, estados SQLite ni la promoción de archivos.
/// </summary>
public interface INoVentaSagaService
{
    Task<NoVentaPvResponseDto> RegistrarAsync(
        UsuarioSesion sesion,
        NoVentaPvFormDto form,
        CancellationToken ct = default);

    Task ProcesarReintentosAsync(CancellationToken ct = default);
}
