using System.Text.Json.Serialization;

namespace SwLicenseWatcher.Core;

public sealed record HealthResponse(
    string Status,
    DateTimeOffset Utc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);

public sealed record DeviceSummary(
    string DeviceCode,
    string HostName,
    string DomainName,
    string OperatingSystem,
    string AgentVersion,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastInventoryUtc,
    string? AssignedHostName = null,
    string? AdminNotes = null,
    string? DeviceId = null,
    string? AssignedDeviceCode = null);

public sealed record DeviceListResponse(
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<DeviceSummary> Items);

public sealed record DeviceDetail(
    string DeviceCode,
    string HostName,
    string DomainName,
    string OperatingSystem,
    string AgentVersion,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastInventoryUtc,
    IReadOnlyList<InstalledSoftwareEntry> InstalledSoftware,
    string? AssignedHostName = null,
    string? AdminNotes = null,
    string? DeviceId = null,
    string? AssignedDeviceCode = null);

public sealed record SoftwareAggregate(
    string Name,
    string? Version,
    string Classification,
    int DeviceCount,
    int CompanyCount = 0,
    int ByoCount = 0,
    int UnassignedCount = 0);

public sealed record SoftwareAggregateListResponse(
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<SoftwareAggregate> Items);

public sealed record SoftwareDevice(
    string DeviceCode,
    string HostName,
    string DomainName,
    string OperatingSystem,
    string AgentVersion,
    DateTimeOffset? LastHeartbeatUtc,
    DateTimeOffset? LastInventoryUtc,
    string? Version,
    string? Publisher,
    string Classification,
    string? LicenseSource = null,
    string? LicenseSourceOverride = null,
    string? AssignedHostName = null);

public sealed record SoftwareDeviceListResponse(
    string Name,
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<SoftwareDevice> Items);

public sealed record PolicyListResponse(
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<SoftwarePolicyEntry> Items);

public sealed record ViolationListResponse(
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<SoftwareViolationEntry> Items);

public sealed record AdminUninstallRequest(
    long Id,
    string DeviceCode,
    string HostName,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string Origin = UninstallGrant.OriginAgent);

public sealed record AgentUninstallCommand(long Id, string Code);

public sealed record UninstallRequestListResponse(
    int Skip,
    int Take,
    int TotalCount,
    IReadOnlyList<AdminUninstallRequest> Items);

public sealed record UninstallRequestCreateRequest(
    string DeviceCode,
    string? DeviceId = null,
    string? DeviceProof = null);

public sealed record UninstallRequestCreatedResponse(
    long Id,
    string DeviceCode,
    string Status,
    DateTimeOffset RequestedAtUtc,
    string Origin = UninstallGrant.OriginAgent);

public sealed record AgentUninstallRequestResponse(
    long Id,
    string DeviceCode,
    string Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Code);

public sealed record UninstallRequestConsumeRequest(
    string DeviceCode,
    string Code,
    string? DeviceId = null,
    string? DeviceProof = null);

public sealed record DeviceProfileWriteRequest(
    string? AssignedHostName,
    string? AdminNotes,
    string? AssignedDeviceCode = null);

public sealed record AgentUserMessageCommand(long Id, string Title, string Body);

public sealed record UserMessageWriteRequest(string Title, string Body);

public sealed record UserMessageCreatedResponse(
    long Id,
    string DeviceCode,
    string Title,
    string Body,
    DateTimeOffset CreatedAtUtc);

public sealed record UserMessageBroadcastResponse(int Count);

public sealed record UserMessageConsumeRequest(
    string DeviceCode,
    string? DeviceId = null,
    string? DeviceProof = null);
