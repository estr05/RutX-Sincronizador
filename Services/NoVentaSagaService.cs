// ============================================================================
// ARCHIVO: NoVentaSagaService.cs
// PROPOSITO: Orquesta la saga idempotente de registro de no-ventas con foto.
//
// Secuencia (8 pasos):
//   1. Validar claims, MIME, tamaño, venta_movil_id.
//   2. Calcular request_hash (SHA-256 de campos + hash_foto + mime).
//   3. Buscar operación existente por venta_movil_id:
//      - Mismo hash  → retornar resultado existente (idempotencia).
//      - Hash distinto → 409 Conflict.
//      - No existe   → continuar.
//   4. CreateOrGetNoSaleOperationAsync (INSERT OR IGNORE atómica).
//   5. GuardarStagingAsync → UpdateNoSaleOperation(media_staged).
//   6. VentaServicePv.RegistrarNoVentaPvAsync (Firebird) → estado firebird_committed.
//   7. PromoverAsync → UpdateMediaFileStatus(completed) → UpdateNoSaleOperation(completed).
//   8. Retornar NoVentaPvResponseDto.
//
// Recuperación por estado existente:
//   MEDIA_SYNCED → intentar pasos 6-8 (foto ya existe en staging).
//   DB_SYNCED    → intentar pasos 7-8 (Firebird ya comprometido).
// ============================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public sealed class NoVentaSagaService : INoVentaSagaService
{
    private readonly IWebSqliteStore _store;
    private readonly IFotoStorageService _fotoStorage;
    private readonly IVentaServicePv _ventaService;
    private readonly ILogger<NoVentaSagaService> _logger;

    private const long TamañoMaximoFoto = 10 * 1024 * 1024; // 10 MB

    private static readonly HashSet<string> MimesPermitidos = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    private static readonly Dictionary<string, string> ExtensionPorMime = new(StringComparer.OrdinalIgnoreCase)
    {
        { "image/jpeg", "jpg" },
        { "image/jpg",  "jpg" },
        { "image/png",  "png" },
        { "image/webp", "webp" },
    };

    public NoVentaSagaService(
        IWebSqliteStore store,
        IFotoStorageService fotoStorage,
        IVentaServicePv ventaService,
        ILogger<NoVentaSagaService> logger)
    {
        _store       = store       ?? throw new ArgumentNullException(nameof(store));
        _fotoStorage = fotoStorage ?? throw new ArgumentNullException(nameof(fotoStorage));
        _ventaService = ventaService ?? throw new ArgumentNullException(nameof(ventaService));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProcesarReintentosAsync(CancellationToken ct = default)
    {
        var pendientes = await _store.ObtenerNoVentasParaReintentoAsync(maxIntentos: 5, limite: 10, ct: ct);
        foreach (var op in pendientes)
        {
            if (op.PayloadJson == null || op.SessionJson == null)
            {
                await _store.UpdateNoSaleOperationAsync(op.Id, "permanently_failed", errorMessage: "Payload o sesion faltante para reintento", ct: ct);
                continue;
            }

            try
            {
                var options = new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                };
                var dto = JsonSerializer.Deserialize<NoVentaPvCreateDto>(op.PayloadJson, options);
                var sesion = JsonSerializer.Deserialize<UsuarioSesion>(op.SessionJson, options);
                
                if (dto == null || sesion == null)
                    throw new Exception("Deserialización nula");

                await ReanudarSagaAsync(op, foto: null, fotoExtension: null, dto, sesion, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Saga] Error al procesar reintento para operación {OpId}", op.Id);
                var nuevoStatus = (op.Attempts + 1 >= 5) ? "dead_letter" : "retryable_failed";
                await _store.UpdateNoSaleOperationAsync(op.Id, nuevoStatus, errorMessage: ex.Message, ct: ct);
            }
        }
    }

    public async Task<NoVentaPvResponseDto> RegistrarAsync(
        UsuarioSesion sesion, NoVentaPvFormDto form, CancellationToken ct = default)
    {
        // ── PASO 1: Validaciones y Deserialización JSON ───────────────────────
        if (string.IsNullOrWhiteSpace(form.PayloadJson))
            throw new ArgumentException("El payload JSON es obligatorio.");

        NoVentaPvCreateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<NoVentaPvCreateDto>(form.PayloadJson, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            if (dto == null || string.IsNullOrWhiteSpace(dto.VentaMovilId))
                throw new Exception("El payload no contiene venta_movil_id.");
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Error al procesar el payload JSON: {ex.Message}");
        }

        IFormFile? foto = form.Foto;
        string? fotoHash = null;
        string? fotoMime = null;
        string? fotoExtension = null;

        if (foto != null && foto.Length > 0)
        {
            if (foto.Length > TamañoMaximoFoto)
                throw new ArgumentException(
                    $"La foto excede el tamaño máximo permitido ({TamañoMaximoFoto / 1024 / 1024} MB).");

            fotoMime = foto.ContentType?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(fotoMime) || !MimesPermitidos.Contains(fotoMime))
                throw new ArgumentException($"Tipo MIME '{fotoMime}' no permitido. Use image/jpeg, image/png o image/webp.");

            fotoExtension = ExtensionPorMime.GetValueOrDefault(fotoMime, "jpg");

            using var ms = new MemoryStream();
            await foto.OpenReadStream().CopyToAsync(ms, ct);
            fotoHash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        // ── PASO 2: Calcular request_hash ────────────────────────────────────
        // El hash se calcula con el JSON crudo + el hash de la foto
        var requestHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(form.PayloadJson + (fotoHash ?? "")))).ToLowerInvariant();

        // ── PASO 3: Verificar idempotencia ───────────────────────────────────
        var operacionExistente = await _store.FindNoSaleOperationAsync(dto.VentaMovilId, ct);
        if (operacionExistente != null)
        {
            if (operacionExistente.RequestHash != requestHash)
            {
                _logger.LogWarning(
                    "[Saga] Conflicto de hash para venta_movil_id={VentaMovilId}: hash existente={HashExistente}, nuevo={HashNuevo}",
                    dto.VentaMovilId, operacionExistente.RequestHash, requestHash);
                throw new InvalidOperationException(
                    $"Ya existe una no-venta con venta_movil_id='{dto.VentaMovilId}' pero con datos distintos.");
            }
            return await ReanudarSagaAsync(operacionExistente, foto, fotoExtension, dto, sesion, ct);
        }

        // ── PASO 4: Crear operación con estado 'PENDING' ──────────────────────
        var sessionJson = JsonSerializer.Serialize(sesion);
        var op = await _store.CreateOrGetNoSaleOperationAsync(
            dto.VentaMovilId, requestHash,
            dto.VendedorId, dto.ClienteId, dto.CausaId,
            dto.FechaHora.ToString("o"),
            form.PayloadJson, sessionJson,
            ct);

        if (op.RequestHash != requestHash)
            throw new InvalidOperationException(
                $"Ya existe una no-venta con venta_movil_id='{dto.VentaMovilId}' con datos distintos.");

        return await ReanudarSagaAsync(op, foto, fotoExtension, dto, sesion, ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // LÓGICA DE REANUDACIÓN POR ESTADO
    // ────────────────────────────────────────────────────────────────────────

    private async Task<NoVentaPvResponseDto> ReanudarSagaAsync(
        NoSaleOperationRow op,
        IFormFile? foto, string? fotoExtension,
        NoVentaPvCreateDto form,
        UsuarioSesion sesion,
        CancellationToken ct)
    {
        string? fotoRelativePath = null;
        if (op.FotoFileId.HasValue)
        {
            var media = await EncontrarMediaPorIdAsync(op.FotoFileId.Value, ct);
            fotoRelativePath = media?.RelativePath;
        }

        // Resultado ya completo
        if (op.Status == "completed")
        {
            return new NoVentaPvResponseDto(
                op.DoctoPvId ?? 0,
                op.Folio ?? "",
                "No Venta registrada exitosamente",
                fotoRelativePath);
        }

        // Si falló permanentemente, la retomamos solo si se proveen nuevos datos o simplemente lanzamos error.
        if (op.Status == "permanently_failed" || op.Status == "dead_letter")
            throw new InvalidOperationException($"La operación está en estado terminal fallido ({op.Status}).");

        long? mediaFileId = op.FotoFileId;

        // ── PASO 5: Guardar foto en disco (staging) ──────────────────────────
        if (op.Status == "pending" || op.Status == "retryable_failed")
        {
            if (foto != null && foto.Length > 0)
            {
                try
                {
                    var staging = await _fotoStorage.GuardarStagingAsync(foto, op.Id, fotoExtension!, ct);
                    var media = await _store.CreateMediaFileAsync(
                        op.Id, "no_sale_photo",
                        foto.FileName, staging.StoredName, staging.RelativePath,
                        foto.ContentType ?? "image/jpeg", staging.SizeBytes, staging.Sha256,
                        ct);
                    op = await _store.UpdateNoSaleOperationAsync(op.Id, "media_staged", fotoFileId: media.Id, ct: ct);
                    mediaFileId = media.Id;
                    fotoRelativePath = media.RelativePath;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Saga] Fallo al guardar foto staging para operación {Id}", op.Id);
                    await _store.UpdateNoSaleOperationAsync(op.Id, "retryable_failed",
                        errorCode: "STAGING_FAILED", errorMessage: ex.Message, ct: ct);
                    throw;
                }
            }
        }

        // ── PASO 6: Insertar en Firebird ─────────────────────────────────────
        if (op.Status == "media_staged" || op.Status == "pending" || op.Status == "retryable_failed")
        {
            try
            {
                var dto = new NoVentaPvCreateDto
                {
                    VentaMovilId   = form.VentaMovilId,
                    VendedorId     = form.VendedorId,
                    ClienteId      = form.ClienteId,
                    FechaHora      = form.FechaHora,
                    CajaId         = sesion.CajaId,
                    CajeroId       = sesion.CajeroId,
                    UsuarioCreador = sesion.Usuario,
                    CausaId        = form.CausaId,
                    CausaDesc      = form.CausaDesc,
                    Comentario     = form.Comentario,
                    FotoPath       = null // foto gestionada por BD/C, no DESCRIPCION
                };

                var (doctoPvId, folio) = await _ventaService.RegistrarNoVentaPvAsync(sesion, dto);
                op = await _store.UpdateNoSaleOperationAsync(
                    op.Id, "media_promotion_pending",
                    doctoPvId: doctoPvId, folio: folio, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Saga] Fallo en Firebird para operación {Id}", op.Id);
                // NO marcar como failed si no estamos seguros. Lo dejamos en su estado o retryable_failed
                await _store.UpdateNoSaleOperationAsync(op.Id, "retryable_failed",
                    errorCode: "FIREBIRD_FAILED", errorMessage: ex.Message, ct: ct);
                throw;
            }
        }

        // ── PASO 7: Promover foto (si hay media y aún no se promovió) ────────
        // IMPORTANTE: si la promoción falla la operación queda en retryable_failed,
        // NUNCA avanza a 'completed' sin confirmación de la foto.
        bool promocionExitosa = false;
        if (op.Status == "media_promotion_pending")
        {
            if (mediaFileId.HasValue)
            {
                var mediaFile = await EncontrarMediaPorIdAsync(mediaFileId.Value, ct);
                if (mediaFile == null)
                {
                    // El registro de media desapareció — error de datos, fallo recuperable.
                    _logger.LogError(
                        "[Saga] MediaFile id={MediaId} no encontrado para operación {Id}; dejando en retryable_failed",
                        mediaFileId.Value, op.Id);
                    await _store.UpdateNoSaleOperationAsync(op.Id, "retryable_failed",
                        errorCode: "MEDIA_NOT_FOUND",
                        errorMessage: $"MediaFile id={mediaFileId.Value} no encontrado en el almacén.",
                        ct: ct);
                    throw new InvalidOperationException(
                        $"[Saga] MediaFile id={mediaFileId.Value} no encontrado. La operación se marcó como retryable_failed.");
                }

                if (mediaFile.Status == "completed")
                {
                    // Ya promovida (reintento idempotente).
                    fotoRelativePath = mediaFile.RelativePath;
                    promocionExitosa = true;
                }
                else if (mediaFile.Status == "staging")
                {
                    try
                    {
                        var relativeFinal = await _fotoStorage.PromoverAsync(mediaFile.StoredName, ct);
                        await _store.UpdateMediaFileStatusAsync(mediaFile.Id, "completed",
                            relativePath: relativeFinal, ct: ct);
                        fotoRelativePath = relativeFinal;
                        promocionExitosa = true;
                    }
                    catch (Exception ex)
                    {
                        // La promoción falló: el documento Firebird está comprometido pero
                        // la foto no llegó a permanent storage. Dejamos en retryable_failed
                        // para que el worker de reintentos lo intente de nuevo.
                        _logger.LogError(ex,
                            "[Saga] Fallo al promover foto (MediaFile id={MediaId}) para operación {Id}; "
                            + "dejando en retryable_failed para reintento automático",
                            mediaFile.Id, op.Id);
                        await _store.UpdateNoSaleOperationAsync(op.Id, "retryable_failed",
                            errorCode: "PROMO_FAILED", errorMessage: ex.Message, ct: ct);
                        // Relanzar para que el caller sepa que no se completó.
                        throw;
                    }
                }
                else
                {
                    // Estado de media inesperado (ej. 'failed'): marcar retryable.
                    _logger.LogWarning(
                        "[Saga] MediaFile id={MediaId} tiene estado inesperado '{Status}' para op {Id}.",
                        mediaFile.Id, mediaFile.Status, op.Id);
                    await _store.UpdateNoSaleOperationAsync(op.Id, "retryable_failed",
                        errorCode: "MEDIA_BAD_STATE",
                        errorMessage: $"Estado de media inesperado: '{mediaFile.Status}'.",
                        ct: ct);
                    throw new InvalidOperationException(
                        $"[Saga] Estado de media inesperado: '{mediaFile.Status}'. Operación marcada como retryable_failed.");
                }
            }
            else
            {
                // Sin foto: la operación Firebird ya está comprometida y no hay archivo que promover.
                promocionExitosa = true;
            }
        }
        else
        {
            // Si el estado no es media_promotion_pending pero llegamos aquí,
            // es un reintento con estado ya resuelto.
            promocionExitosa = true;
        }

        // ── PASO 8: Marcar operación como completada ──────────────────────────
        // Solo se ejecuta cuando la promoción fue exitosa (o no había foto).
        // Si llegamos aquí sin 'promocionExitosa', es un bug de lógica — protección defensiva.
        if (!promocionExitosa)
        {
            throw new InvalidOperationException(
                "[Saga] Se alcanzó el paso 8 sin promoción confirmada. Bug de lógica detectado.");
        }

        op = await _store.UpdateNoSaleOperationAsync(op.Id, "completed", ct: ct);

        _logger.LogInformation(
            "[Saga] Completada: venta_movil_id={VentaMovilId}, DoctoPvId={DoctoPvId}, Folio={Folio}",
            op.VentaMovilId, op.DoctoPvId, op.Folio);

        return new NoVentaPvResponseDto(
            op.DoctoPvId!.Value,
            op.Folio ?? "",
            "No Venta registrada exitosamente",
            fotoRelativePath);
    }

    // ────────────────────────────────────────────────────────────────────────
    // HELPERS
    // ────────────────────────────────────────────────────────────────────────

    private async Task<MediaFileRow?> EncontrarMediaPorIdAsync(long mediaFileId, CancellationToken ct)
    {
        return await _store.FindMediaFileByIdAsync(mediaFileId, ct);
    }
}
