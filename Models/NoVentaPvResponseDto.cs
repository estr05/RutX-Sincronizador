namespace Rutx.Sincronizador.Models;

/// <summary>
/// Respuesta de POST /api/v1/pv/noventa (contrato retro-compatible).
///
/// Campos originales:
///   docto_pv_id, folio, mensaje
///
/// Extensión aditiva (nullable):
///   foto_relative_path — ruta relativa de la foto promovida (null si sin foto).
///
/// La serialización SnakeCaseLower del middleware produce exactamente:
///   { "docto_pv_id": int, "folio": "...", "mensaje": "...", "foto_relative_path": null }
/// </summary>
public sealed record NoVentaPvResponseDto(
    int DoctoPvId,
    string Folio,
    string Mensaje,
    string? FotoRelativePath = null);
