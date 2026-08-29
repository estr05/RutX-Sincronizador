using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public class TelemetryService : ITelemetryService
{
    private const int MaxMetadataKeys = 20;

    private readonly IWebSqliteStore _store;

    public TelemetryService(IWebSqliteStore store)
    {
        _store = store;
    }

    public async Task<TelemetryEventResponse> ProcessEventAsync(TelemetryEventRequest request, int sellerId, CancellationToken ct = default)
    {
        // 1. Validar tipo de evento
        if (request.EventType is not ("venta" or "no_venta" or "descarga_matutina" or "cierre_jornada"))
            throw new ArgumentException("Tipo de evento no válido.");

        // 2. Validar tamaño de Metadata (S3: evitar payloads gigantes)
        if (request.Metadata?.Count > MaxMetadataKeys)
            throw new ArgumentException($"Metadata excede el límite de {MaxMetadataKeys} claves.");

        // 3. Validar CustomerId requerido para ventas
        if (request.EventType is "venta" or "no_venta" && request.CustomerId == null)
            throw new ArgumentException("Se requiere CustomerId para este tipo de evento.");

        // 4. (Removido) Ya no se requiere contrato/DeviceAssignment. El JWT valida al vendedor.

        // 5. Calcular Hash solo con campos estables (R5: idempotencia fiable en reintentos)
        string payloadHash = ComputeStableHash(request.ClientEventId, request.EventType, request.CustomerId, request.OccurredAt);
        string? metaJson = request.Metadata != null ? JsonSerializer.Serialize(request.Metadata) : null;

        // 6. Registrar Evento (INSERT OR IGNORE = idempotente)
        var eventRow = new SellerEventRow(
            Id: 0,
            ClientEventId: request.ClientEventId,
            EventType: request.EventType,
            SellerId: sellerId,
            DeviceAssignmentId: null, // Se quita dependencia de contrato
            CustomerId: request.CustomerId,
            RelatedEntityId: request.RelatedEntityId,
            PayloadHash: payloadHash,
            MetadataJson: metaJson,
            OccurredAt: request.OccurredAt,
            Status: "received",
            CreatedAt: "",
            UpdatedAt: ""
        );

        var (result, row) = await _store.RegisterEventAsync(eventRow, ct);

        if (result == "conflict")
            throw new InvalidOperationException("Conflicto de idempotencia.");

        // 7. Registrar Ubicación (Best-effort — no propaga fallo)
        if (result == "inserted" && request.Latitude.HasValue && request.Longitude.HasValue)
        {
            try
            {
                await _store.InsertLocationForEventAsync(
                    eventId: row.Id,
                    sellerId: sellerId,
                    sessionId: null,
                    latitude: request.Latitude.Value,
                    longitude: request.Longitude.Value,
                    accuracy: request.Accuracy,
                    occurredAt: request.OccurredAt,
                    sourceEventType: request.EventType,
                    customerId: request.CustomerId,
                    ct: ct);
            }
            catch
            {
                // Silencioso — la ubicación no es crítica
            }
        }

        return new TelemetryEventResponse(
            Status: "processed",
            Duplicate: result == "duplicate_same_hash");
    }

    private static string ComputeStableHash(string clientEventId, string eventType, int? customerId, string occurredAt)
    {
        string raw = $"{clientEventId}|{eventType}|{customerId}|{occurredAt}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
