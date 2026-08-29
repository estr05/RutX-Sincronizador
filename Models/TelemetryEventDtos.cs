using System.Collections.Generic;

namespace Rutx.Sincronizador.Models;

public sealed record TelemetryEventRequest(
    string ClientEventId,
    string EventType,
    int? CustomerId,
    string? RelatedEntityId,
    double? Latitude,
    double? Longitude,
    double? Accuracy,
    string OccurredAt,
    string DeviceInstallationId,
    Dictionary<string, object?>? Metadata);

public sealed record TelemetryEventResponse(
    string Status,
    bool Duplicate = false);
