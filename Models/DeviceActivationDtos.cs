namespace Rutx.Sincronizador.Models;

public sealed record DeviceActivationRequest(
    string ContractNumber,
    string ActivationKey,
    string DeviceInstallationId,
    string Platform,
    string AppVersion);

public sealed record DeviceActivationResponse(
    int DeviceNumber,
    string Status);
