using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public class DeviceRegistryService : IDeviceRegistryService
{
    private readonly IWebSqliteStore _store;

    public DeviceRegistryService(IWebSqliteStore store)
    {
        _store = store;
    }

    public async Task<DeviceActivationResponse> ActivateDeviceAsync(DeviceActivationRequest request, int sellerId, CancellationToken ct = default)
    {
        // 1. Validar el contrato
        var contract = await _store.GetContractByNumberAsync(request.ContractNumber, ct);
        if (contract == null || contract.Status != "active")
        {
            throw new InvalidOperationException("Contrato inválido o inactivo.");
        }

        // 2. Guardar HASH de la clave (nunca el valor en claro)
        string tokenHash = ComputeSha256(request.ActivationKey);

        // 3. Registrar o actualizar dispositivo
        var device = await _store.UpsertMobileDeviceAsync(
            request.DeviceInstallationId,
            request.Platform,
            request.AppVersion,
            tokenHash,
            ct);

        // 4. Buscar asignación activa existente para este dispositivo
        var existingAssignment = await _store.GetActiveAssignmentAsync(request.DeviceInstallationId, ct);
        if (existingAssignment != null)
        {
            if (existingAssignment.ContractId == contract.Id && existingAssignment.SellerId == sellerId)
            {
                return new DeviceActivationResponse(existingAssignment.DeviceNumber, existingAssignment.Status);
            }
            // Si estaba asignado a otro, revocar
            await _store.RevokeAssignmentAsync(existingAssignment.Id, ct);
        }

        // 5. Verificar límite de dispositivos del contrato
        int activeCount = await _store.CountActiveAssignmentsAsync(contract.Id, ct);
        if (activeCount >= contract.MaxActiveDevices)
        {
            throw new InvalidOperationException("Límite de dispositivos alcanzado para este contrato.");
        }

        // 6. Crear nueva asignación
        var newAssignment = await _store.CreateAssignmentAsync(
            device.Id,
            contract.Id,
            activeCount + 1,
            sellerId,
            ct);

        return new DeviceActivationResponse(newAssignment.DeviceNumber, newAssignment.Status);
    }

    private static string ComputeSha256(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
