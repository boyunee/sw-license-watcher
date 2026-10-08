using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Application;

public interface IHealthProbe
{
    Task ProbeAsync(CancellationToken cancellationToken);
}

public interface ISnapshotRepository
{
    Task<SnapshotSaveResult> SaveSnapshotAsync(
        InventoryIngestionRequest snapshot,
        CancellationToken cancellationToken);
}

public interface IHeartbeatRepository
{
    Task SaveHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken);

    Task<List<StalePcHeartbeat>> GetStaleHeartbeatsAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken);

    Task<List<StalePcHeartbeat>> ClaimNewlyStaleHeartbeatsAsync(
        DateTimeOffset cutoff,
        DateTimeOffset notifiedAtUtc,
        CancellationToken cancellationToken);
}

public interface IDeviceQuery
{
    Task<(int TotalCount, List<DeviceSummary> Items)> ListDevicesAsync(
        int skip,
        int take,
        string? search,
        int? staleAfterHours,
        CancellationToken cancellationToken);

    Task<DeviceDetail?> GetDeviceAsync(
        string deviceCode,
        string? classification,
        CancellationToken cancellationToken);

    Task<DeviceAgentAssignment?> GetDeviceAssignmentAsync(
        string deviceCode,
        string? deviceId,
        CancellationToken cancellationToken);

    Task<DeviceEnrollmentKeys?> GetDeviceEnrollmentKeysAsync(
        string deviceCode,
        string? deviceId,
        CancellationToken cancellationToken);

    Task<DeviceProfileUpdateResult> UpdateDeviceProfileAsync(
        string deviceCode,
        DeviceProfileWriteRequest request,
        CancellationToken cancellationToken);

    Task BindDeviceEnrollmentAsync(
        string deviceCode,
        string? deviceId,
        string devicePublicKey,
        string deviceCertificate,
        string issuedDeviceId,
        CancellationToken cancellationToken);
}

public interface ISoftwareQuery
{
    Task<(int TotalCount, List<SoftwareAggregate> Items)> ListSoftwareAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken);

    Task<(int TotalCount, List<SoftwareDevice> Items)> ListSoftwareDevicesAsync(
        string name,
        int skip,
        int take,
        string? classification,
        CancellationToken cancellationToken);

    Task<(int TotalCount, List<SoftwareAggregateAssets> Items)> ListSoftwareWithAssetsAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken);
}

public interface IViolationQuery
{
    Task<(int TotalCount, List<SoftwareViolationEntry> Items)> ListViolationsAsync(
        int skip,
        int take,
        string? search,
        DateTimeOffset? since,
        CancellationToken cancellationToken);
}

public interface IPolicyStore
{
    Task<(int TotalCount, List<SoftwarePolicyEntry> Items)> ListPoliciesAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken);

    Task<SoftwarePolicyEntry?> GetPolicyAsync(long id, CancellationToken cancellationToken);

    Task<SoftwarePolicyEntry> CreatePolicyAsync(
        SoftwarePolicyWriteRequest request,
        CancellationToken cancellationToken);

    Task<SoftwarePolicyEntry?> UpdatePolicyAsync(
        long id,
        SoftwarePolicyWriteRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeletePolicyAsync(long id, CancellationToken cancellationToken);

    Task<SoftwarePolicyEntry> UpsertSoftwareClassificationAsync(
        string productName,
        SoftwareClassificationWriteRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SoftwarePolicyEntry>> UpsertSoftwareClassificationsAsync(
        IReadOnlyList<(string Name, SoftwareClassificationWriteRequest Request)> items,
        CancellationToken cancellationToken);

    Task<bool> SetDeviceSoftwareLicenseSourceAsync(
        string deviceCode,
        string softwareName,
        string? licenseSource,
        CancellationToken cancellationToken);
}

public interface IUninstallRequestStore
{
    Task<UninstallRequestCreatedResponse?> CreateUninstallRequestAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<AgentUninstallRequestResponse?> GetAgentUninstallRequestAsync(
        long id,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<bool> ConsumeUninstallRequestAsync(
        long id,
        string deviceCode,
        string code,
        CancellationToken cancellationToken);

    Task<(int TotalCount, List<AdminUninstallRequest> Items)> ListUninstallRequestsAsync(
        int skip,
        int take,
        string? search,
        CancellationToken cancellationToken);

    Task<bool> ApproveUninstallRequestAsync(long id, CancellationToken cancellationToken);

    Task<bool> DenyUninstallRequestAsync(long id, CancellationToken cancellationToken);

    Task<UninstallRequestCreatedResponse?> CreateDirectedUninstallRequestAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<AgentUninstallCommand?> GetDirectedUninstallCommandAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<bool> CancelDirectedUninstallRequestAsync(long id, CancellationToken cancellationToken);
}

public sealed record EnqueuedUserMessage(
    AgentUserMessageCommand Command,
    IReadOnlyList<string> DeviceAliases);

public interface IUserMessageStore
{
    Task<EnqueuedUserMessage?> EnqueueAsync(
        string deviceCode,
        string title,
        string body,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<EnqueuedUserMessage>> BroadcastAsync(
        string title,
        string body,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentUserMessageCommand>> ListPendingAsync(
        string deviceCode,
        int take,
        CancellationToken cancellationToken);

    Task<AgentUserMessageCommand?> GetOldestPendingAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<bool> ConsumeAsync(
        long id,
        string deviceCode,
        CancellationToken cancellationToken);
}

public interface IWorkerUpdatePinStore
{
    Task<UpdateManifest?> GetWorkerUpdatePinAsync(
        string targetServiceName,
        CancellationToken cancellationToken);

    Task<UpdateManifest> UpsertWorkerUpdatePinAsync(
        UpdateManifest pin,
        CancellationToken cancellationToken);

    Task<bool> SeedWorkerUpdatePinIfEmptyAsync(
        UpdateManifest pin,
        CancellationToken cancellationToken);
}
