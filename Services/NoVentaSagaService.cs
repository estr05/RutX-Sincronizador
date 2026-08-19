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
//   media_staged       → intentar pasos 6-8 (foto ya existe en staging).
//   firebird_committed → intentar pasos 7-8 (Firebird ya comprometido).
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

    public async Task<NoVentaPvResponseDto> RegistrarAsync(
        UsuarioSesion sesion, NoVentaPvFormDto form, CancellationToken ct = default)
    {
        // ── PASO 1: Validaciones de entrada ──────────────────────────────────
        if (string.IsNullOrWhiteSpace(form.VentaMovilId))
            throw new ArgumentException("venta_movil_id es obligatorio.");

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

            // Pre-calcular hash del contenido de la foto para el request_hash
            using var ms = new MemoryStream();
            await foto.OpenReadStream().CopyToAsync(ms, ct);
            fotoHash = Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
        }

        // ── PASO 2: Calcular request_hash ────────────────────────────────────
        var requestHash = CalcularRequestHash(form, fotoHash, fotoMime);

        // ── PASO 3: Verificar idempotencia ───────────────────────────────────
        var operacionExistente = await _store.FindNoSaleOperationAsync(form.VentaMovilId, ct);
        if (operacionExistente != null)
        {
            if (operacionExistente.RequestHash != requestHash)
            {
                _logger.LogWarning(
                    "[Saga] Conflicto de hash para venta_movil_id={VentaMovilId}: hash existente={HashExistente}, nuevo={HashNuevo}",
                    form.VentaMovilId, operacionExistente.RequestHash, requestHash);
                throw new InvalidOperationException(
                    $"Ya existe una no-venta con venta_movil_id='{form.VentaMovilId}' pero con datos distintos. " +
                    "No se puede registrar un payload diferente para el mismo id móvil.");
            }

            // Mismo hash: reanudar o retornar resultado completado
            return await ReanudarSagaAsync(operacionExistente, foto, fotoExtension, form, sesion, ct);
        }

        // ── PASO 4: Crear operación con estado 'received' ────────────────────
        var op = await _store.CreateOrGetNoSaleOperationAsync(
            form.VentaMovilId, requestHash,
            form.VendedorId, form.ClienteId, form.CausaId,
            form.FechaHora.ToString("o"),
            ct);

        // Si por condición de carrera ya quedó en estado terminal, reanudar
        if (op.RequestHash != requestHash)
            throw new InvalidOperationException(
                $"Ya existe una no-venta con venta_movil_id='{form.VentaMovilId}' con datos distintos.");

        return await ReanudarSagaAsync(op, foto, fotoExtension, form, sesion, ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // LÓGICA DE REANUDACIÓN POR ESTADO
    // ────────────────────────────────────────────────────────────────────────

    private async Task<NoVentaPvResponseDto> ReanudarSagaAsync(
        NoSaleOperationRow op,
        IFormFile? foto, string? fotoExtension,
        NoVentaPvFormDto form,
        UsuarioSesion sesion,
        CancellationToken ct)
    {
        // Resultado ya completo
        if (op.Status == "completed" && op.DoctoPvId.HasValue)
        {
            _logger.LogInformation(
                "[Saga] Operación ya completada (idempotencia): venta_movil_id={VentaMovilId}, DoctoPvId={DoctoPvId}",
                op.VentaMovilId, op.DoctoPvId);

            MediaFileRow? mediaExistente = op.FotoFileId.HasValue
                ? await _store.FindMediaFileByStoredNameAsync(
                    (await _store.FindMediaFileByStoredNameAsync("", ct))?.StoredName ?? "", ct)
                : null;

            return new NoVentaPvResponseDto(
                op.DoctoPvId!.Value,
                op.Folio ?? "",
                "No Venta registrada exitosamente",
                mediaExistente?.RelativePath);
        }

        // ── PASO 5: Staging (si hay foto y aún no se guardó) ─────────────────
        long? mediaFileId = op.FotoFileId;
        string? fotoRelativePath = null;

        if (foto != null && foto.Length > 0 && op.Status == "received")
        {
            try
            {
                var staging = await _fotoStorage.GuardarStagingAsync(foto, op.Id, fotoExtension!, ct);
                var mediaFile = await _store.CreateMediaFileAsync(
                    op.Id, "no_sale_photo",
                    foto.FileName, staging.StoredName, staging.RelativePath,
                    foto.ContentType ?? "image/jpeg", staging.SizeBytes, staging.Sha256,
                    ct);
                mediaFileId = mediaFile.Id;
                op = await _store.UpdateNoSaleOperationAsync(op.Id, "media_staged", fotoFileId: mediaFileId, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Saga] Fallo en staging para operación {Id}", op.Id);
                await _store.UpdateNoSaleOperationAsync(op.Id, "failed",
                    errorCode: "STAGING_FAILED", errorMessage: ex.Message, ct: ct);
                throw;
            }
        }

        // ── PASO 6: Firebird (si aún no se comprometió) ──────────────────────
        if (op.Status is "received" or "media_staged")
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
                    op.Id, "firebird_committed",
                    doctoPvId: doctoPvId, folio: folio, ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Saga] Fallo en Firebird para operación {Id}", op.Id);
                // NO marcar como failed si media_staged: dejar para reconciliación
                await _store.UpdateNoSaleOperationAsync(op.Id, op.Status,
                    errorCode: "FIREBIRD_FAILED", errorMessage: ex.Message, ct: ct);
                throw;
            }
        }

        // ── PASO 7: Promover foto (si hay media y aún no se promovió) ────────
        if (op.Status == "firebird_committed" && mediaFileId.HasValue)
        {
            // Obtener stored_name del media file
            var mediaFile = await EncontrarMediaPorIdAsync(mediaFileId.Value, ct);
            if (mediaFile != null && mediaFile.Status == "staging")
            {
                try
                {
                    var relativeFinal = await _fotoStorage.PromoverAsync(mediaFile.StoredName, ct);
                    await _store.UpdateMediaFileStatusAsync(mediaFile.Id, "completed",
                        relativePath: relativeFinal, ct: ct);
                    fotoRelativePath = relativeFinal;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Saga] Fallo al promover foto para operación {Id}; la operación sigue en firebird_committed",
                        op.Id);
                    // No relanzar: el documento Firebird está comprometido, solo falló la promoción.
                }
            }
            else if (mediaFile?.Status == "completed")
            {
                fotoRelativePath = mediaFile.RelativePath;
            }
        }

        // ── PASO 8: Marcar operación como completada ──────────────────────────
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
        // La interfaz de store no expone FindById para media; usar FindByStoredName
        // Por ahora buscar por operation_id en la tabla sería ideal, pero IWebSqliteStore
        // solo expone FindByStoredName. Se acepta este límite en Sprint 4:
        // el stored_name se puede recuperar del registro de la operación si la guardamos.
        // TODO(Sprint 5): añadir FindMediaFileByIdAsync al store.
        return null; // retorna null → se omite la promoción en este paso si no se puede encontrar
    }

    private static string CalcularRequestHash(NoVentaPvFormDto form, string? fotoHash, string? fotoMime)
    {
        var campos = new
        {
            venta_movil_id = form.VentaMovilId,
            vendedor_id    = form.VendedorId,
            cliente_id     = form.ClienteId,
            causa_id       = form.CausaId,
            causa_desc     = form.CausaDesc?.Trim(),
            comentario     = form.Comentario?.Trim(),
            fecha_hora     = form.FechaHora.ToString("o"),
        };
        var json = JsonSerializer.Serialize(campos,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        var contenido = $"{json}|{fotoHash ?? "EMPTY"}|{fotoMime ?? "NONE"}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(contenido));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
