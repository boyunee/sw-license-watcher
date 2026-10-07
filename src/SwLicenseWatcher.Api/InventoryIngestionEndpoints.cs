using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api;

internal static class InventoryIngestionEndpoints
{
    internal static IEndpointRouteBuilder MapInventoryIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(AgentPaths.InventorySnapshots, async (
            InventoryIngestionRequest request,
            InventoryMemoryStore store,
            ISnapshotRepository repository,
            NotificationPublisher notifications,
            DeviceEnrollmentService enrollment,
            IUninstallRequestStore uninstallRequests,
            IUserMessageStore userMessages,
            CancellationToken cancellationToken) =>
        {
            if (!InventorySnapshotValidator.TryValidate(request, out var validationError))
            {
                return Results.BadRequest(validationError);
            }

            var identityError = await enrollment.AuthorizeIngestionAsync(
                request.Pc.DeviceCode,
                request.Pc.DeviceId,
                request.Pc.DevicePublicKey,
                request.Pc.DeviceCertificate,
                request.Pc.DeviceProof,
                cancellationToken);
            if (identityError is not null)
            {
                return Results.BadRequest(identityError);
            }

            var saveResult = await repository.SaveSnapshotAsync(request, cancellationToken);
            store.RecordSnapshot(request);
            notifications.EnqueueNewSoftwareIfNeeded(request, saveResult);
            notifications.EnqueueBlacklistViolationsIfNeeded(request, saveResult);
            var assignment = await enrollment.CompleteAsync(
                request.Pc.DeviceCode, request.Pc.DeviceId, request.Pc.DevicePublicKey, cancellationToken);
            var deviceCode = assignment?.DeviceCode ?? request.Pc.DeviceCode;
            var uninstallCommand = await uninstallRequests.GetDirectedUninstallCommandAsync(
                deviceCode, cancellationToken);
            var userMessageCommand = await userMessages.GetOldestPendingAsync(deviceCode, cancellationToken);
            return Results.Accepted($"/api/inventory/devices/{deviceCode}", new SnapshotAcceptedResponse(
                deviceCode,
                request.InstalledSoftware.Count,
                request.CollectedAtUtc,
                assignment?.AssignedHostName,
                assignment?.DeviceCode,
                assignment?.DeviceId,
                assignment?.DeviceCertificate,
                uninstallCommand,
                userMessageCommand));
        });
        endpoints.MapPost(AgentPaths.Heartbeats, async (
            AgentHeartbeat heartbeat,
            InventoryMemoryStore store,
            IHeartbeatRepository repository,
            DeviceEnrollmentService enrollment,
            IUninstallRequestStore uninstallRequests,
            IUserMessageStore userMessages,
            CancellationToken cancellationToken) =>
        {
            if (!InventorySnapshotValidator.TryValidate(heartbeat, out var validationError))
            {
                return Results.BadRequest(validationError);
            }

            var identityError = await enrollment.AuthorizeIngestionAsync(
                heartbeat.DeviceCode,
                heartbeat.DeviceId,
                heartbeat.DevicePublicKey,
                heartbeat.DeviceCertificate,
                heartbeat.DeviceProof,
                cancellationToken);
            if (identityError is not null)
            {
                return Results.BadRequest(identityError);
            }

            await repository.SaveHeartbeatAsync(heartbeat, cancellationToken);
            store.RecordHeartbeat(heartbeat);
            var assignment = await enrollment.CompleteAsync(
                heartbeat.DeviceCode, heartbeat.DeviceId, heartbeat.DevicePublicKey, cancellationToken);
            var deviceCode = assignment?.DeviceCode ?? heartbeat.DeviceCode;
            var uninstallCommand = await uninstallRequests.GetDirectedUninstallCommandAsync(
                deviceCode, cancellationToken);
            var userMessageCommand = await userMessages.GetOldestPendingAsync(deviceCode, cancellationToken);
            return Results.Accepted($"/api/agents/heartbeats/{deviceCode}", new AgentHeartbeatAcceptedResponse(
                deviceCode,
                heartbeat.HostName,
                heartbeat.ServiceName,
                heartbeat.Version,
                heartbeat.ReportedAtUtc,
                heartbeat.Status,
                assignment?.AssignedHostName,
                assignment?.DeviceCode,
                assignment?.DeviceId,
                assignment?.DeviceCertificate,
                uninstallCommand,
                userMessageCommand));
        });

        return endpoints;
    }
}
