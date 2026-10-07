using System.ComponentModel.DataAnnotations;

namespace SwLicenseWatcher.Core;

public sealed class WorkerAgentOptions
{
    [Required]
    public string DeviceCode { get; set; } = Environment.MachineName;

    [Required]
    public string ServerBaseUrl { get; set; } = "http://localhost:5080";

    public string SnapshotPath { get; set; } = "/api/inventory/snapshots";

    public string HeartbeatPath { get; set; } = "/api/agents/heartbeats";

    public string DomainName { get; set; } = "WORKGROUP";

    public string EventsPath { get; set; } = "/api/agents/events";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMinutes(5);

    public bool RunOnceForDiagnostics { get; set; }

    public string ApiToken { get; set; } = string.Empty;

    public string HealthFilePath { get; set; } = @"C:\ProgramData\SwLicenseWatcher\state\worker-health.json";
}

public sealed class WatchdogOptions
{
    [Required]
    public string DeviceCode { get; set; } = Environment.MachineName;

    [Required]
    public string ServerBaseUrl { get; set; } = "http://localhost:5080";

    public string ManifestPath { get; set; } = "/api/updates/worker/manifest";

    public string WorkerServiceName { get; set; } = "SwLicenseWatcher.Agent.Worker";

    public string StagingDirectory { get; set; } = @"C:\ProgramData\SwLicenseWatcher\staging";

    public string BackupDirectory { get; set; } = @"C:\ProgramData\SwLicenseWatcher\backup";

    public string WorkerInstallDirectory { get; set; } = @"C:\Program Files\SwLicenseWatcher\Agent.Worker";

    public string WorkerHealthFilePath { get; set; } = @"C:\ProgramData\SwLicenseWatcher\state\worker-health.json";

    public string ApiToken { get; set; } = string.Empty;

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(4);

    public TimeSpan MaxJitter { get; set; } = TimeSpan.FromMinutes(60);

    public long MaxPackageBytes { get; set; } = 512 * 1024 * 1024;

    public long MaxExtractedBytes { get; set; } = 1024 * 1024 * 1024;

    public bool RunOnceForDiagnostics { get; set; }
}

public sealed class LocalStateStoreOptions
{
    public string InstanceName { get; set; } = "SwLicenseWatcher";

    public string QueueDirectory { get; set; } = @"C:\ProgramData\SwLicenseWatcher\state\queue";

    public string DpapiScope { get; set; } = "LocalMachine";

    public int MaxQueuedSnapshots { get; set; } = 48;

    public long MaxQueueBytes { get; set; } = 64 * 1024 * 1024;
}

public sealed class ApiSecurityOptions
{
    public string Token { get; set; } = string.Empty;

    public string AgentToken { get; set; } = string.Empty;

    public string AdminToken { get; set; } = string.Empty;

    public bool RequireHttps { get; set; } = true;

    public ApiJwtOptions Jwt { get; set; } = new();
}

public sealed class ApiJwtOptions
{
    public string Authority { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string MetadataAddress { get; set; } = string.Empty;

    public string RequiredScope { get; set; } = string.Empty;

    public string RequiredRole { get; set; } = string.Empty;

    public string RoleClaimType { get; set; } = "roles";

    public string NameClaimType { get; set; } = "name";

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority);

    public string ResolveMetadataAddress()
    {
        if (!string.IsNullOrWhiteSpace(MetadataAddress))
        {
            return MetadataAddress.TrimEnd('/');
        }

        return string.Concat(Authority.TrimEnd('/'), "/.well-known/openid-configuration");
    }
}

public static class ApiSecurityOptionsValidator
{
    public const int MinimumTokenLength = 32;

    public static bool HasUsableToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length >= MinimumTokenLength;

    public static bool HasRequiredRoleTokens(ApiSecurityOptions options) =>
        HasUsableToken(options.AgentToken) &&
        HasUsableToken(options.AdminToken);

    public static bool HasValidConfiguredTokenLengths(ApiSecurityOptions options) =>
        IsMissingOrUsable(options.AgentToken) &&
        IsMissingOrUsable(options.AdminToken);

    public static bool RejectsLegacySharedToken(ApiSecurityOptions options) =>
        string.IsNullOrWhiteSpace(options.Token);

    public static bool HasDistinctRoleTokens(ApiSecurityOptions options) =>
        !string.Equals(options.AgentToken, options.AdminToken, StringComparison.Ordinal);

    public static bool HasValidJwt(ApiSecurityOptions options)
    {
        var jwt = options.Jwt;
        if (!jwt.IsEnabled)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(jwt.Audience) || jwt.ClockSkew < TimeSpan.Zero)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(jwt.RequiredScope) && string.IsNullOrWhiteSpace(jwt.RequiredRole))
        {
            return false;
        }

        return IsHttpsAbsoluteUri(jwt.Authority) &&
            (string.IsNullOrWhiteSpace(jwt.MetadataAddress) ||
             IsHttpsAbsoluteUri(jwt.MetadataAddress));
    }

    private static bool IsMissingOrUsable(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length >= MinimumTokenLength;

    private static bool IsHttpsAbsoluteUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;
}

public sealed class UpdateManifestOptions
{
    public string TargetServiceName { get; set; } = "SwLicenseWatcher.Agent.Worker";

    public string Version { get; set; } = "1.0.0";

    [Required]
    public string PackageUrl { get; set; } = "https://updates.example.local/sw-license-watcher/worker-1.0.0.zip";

    public string Sha256 { get; set; } = "REPLACE_WITH_RELEASE_SHA256";

    public bool RequireAuthenticode { get; set; } = true;

    public int RollbackAfterMinutes { get; set; } = 10;

    public UpdateManifest ToManifest() =>
        new(TargetServiceName, Version, PackageUrl, Sha256, RequireAuthenticode, RollbackAfterMinutes);
}

public sealed class GitHubUpdateOptions
{
    public string Owner { get; set; } = string.Empty;

    public string Repository { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string PackageDirectory { get; set; } = "update-packages";

    public long MaxPackageBytes { get; set; } = 512 * 1024 * 1024;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public int ReleaseListLimit { get; set; } = 15;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Owner) && !string.IsNullOrWhiteSpace(Repository);
}

public sealed class DatabaseOptions
{
    public bool ApplySchemaOnStartup { get; set; }

    public bool ApplySchemaInBackground { get; set; } = true;

    public TimeSpan SchemaReconcileRetry { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class SqlServerStorageOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    public string SchemaName { get; set; } = "inventory";

    public PcTableOptions PcTable { get; set; } = new();

    public InstalledSoftwareTableOptions InstalledSoftwareTable { get; set; } = new();

    public SoftwarePolicyTableOptions SoftwarePolicyTable { get; set; } = new();

    public SoftwareViolationTableOptions SoftwareViolationTable { get; set; } = new();

    public StaleHeartbeatNotificationTableOptions StaleHeartbeatNotificationTable { get; set; } = new();

    public UninstallRequestTableOptions UninstallRequestTable { get; set; } = new();

    public SoftwareLicenseTableOptions SoftwareLicenseTable { get; set; } = new();

    public WorkerUpdatePinTableOptions WorkerUpdatePinTable { get; set; } = new();

    public UserMessageTableOptions UserMessageTable { get; set; } = new();
}

public sealed class PcTableOptions
{
    public string TableName { get; set; } = "pc_entity";

    public string PrimaryKeyColumn { get; set; } = "pc_id";

    public string DeviceCodeColumn { get; set; } = "device_code";

    public string HostNameColumn { get; set; } = "host_name";

    public string DomainNameColumn { get; set; } = "domain_name";

    public string OperatingSystemColumn { get; set; } = "operating_system";

    public string AgentVersionColumn { get; set; } = "agent_version";

    public string LastHeartbeatUtcColumn { get; set; } = "last_heartbeat_utc";

    public string LastInventoryUtcColumn { get; set; } = "last_inventory_utc";

    public string AssignedHostNameColumn { get; set; } = "assigned_host_name";

    public string AdminNotesColumn { get; set; } = "admin_notes";

    public string AssignedDeviceCodeColumn { get; set; } = "assigned_device_code";

    public string DeviceIdColumn { get; set; } = "device_id";

    public string DevicePublicKeyColumn { get; set; } = "device_public_key";

    public string DeviceCertificateColumn { get; set; } = "device_certificate";
}

public sealed class DeviceEnrollmentOptions
{
    public string CaKeyPath { get; set; } = "device-ca.mldsa87.json";
}

public sealed class InstalledSoftwareTableOptions
{
    public string TableName { get; set; } = "pc_installed_sw";

    public string PrimaryKeyColumn { get; set; } = "installed_sw_id";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string DisplayNameColumn { get; set; } = "display_name";

    public string DisplayVersionColumn { get; set; } = "display_version";

    public string PublisherColumn { get; set; } = "publisher";

    public string InstallLocationColumn { get; set; } = "install_location";

    public string DiscoveryScopeColumn { get; set; } = "discovery_scope";

    public string DiscoverySourceColumn { get; set; } = "discovery_source";

    public string ClassificationColumn { get; set; } = "classification";

    public string CollectedAtUtcColumn { get; set; } = "collected_at_utc";
}

public sealed class SoftwarePolicyTableOptions
{
    public string TableName { get; set; } = "software_policy_list";

    public string PrimaryKeyColumn { get; set; } = "policy_id";

    public string ClassificationColumn { get; set; } = "classification";

    public string ProductNameColumn { get; set; } = "product_name";

    public string PublisherColumn { get; set; } = "publisher";

    public string VersionPatternColumn { get; set; } = "version_pattern";

    public string NotesColumn { get; set; } = "notes";

    public string EnabledColumn { get; set; } = "enabled";

    public string UpdatedAtUtcColumn { get; set; } = "updated_at_utc";

    public string DefaultLicenseSourceColumn { get; set; } = "default_license_source";
}

public sealed class SoftwareViolationTableOptions
{
    public string TableName { get; set; } = "software_violation";

    public string PrimaryKeyColumn { get; set; } = "violation_id";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string PolicyForeignKeyColumn { get; set; } = "policy_id";

    public string DisplayNameColumn { get; set; } = "display_name";

    public string DisplayVersionColumn { get; set; } = "display_version";

    public string PublisherColumn { get; set; } = "publisher";

    public string DetectedAtUtcColumn { get; set; } = "detected_at_utc";

    public string LastSeenAtUtcColumn { get; set; } = "last_seen_at_utc";
}

public sealed class StaleHeartbeatNotificationTableOptions
{
    public string TableName { get; set; } = "stale_heartbeat_notification";

    public string PrimaryKeyColumn { get; set; } = "stale_heartbeat_notification_id";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string NotifiedAtUtcColumn { get; set; } = "notified_at_utc";
}

public sealed class UninstallRequestTableOptions
{
    public string TableName { get; set; } = "pc_uninstall_request";

    public string PrimaryKeyColumn { get; set; } = "uninstall_request_id";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string StatusColumn { get; set; } = "status";

    public string RequestedAtUtcColumn { get; set; } = "requested_at_utc";

    public string ApprovedAtUtcColumn { get; set; } = "approved_at_utc";

    public string ConsumedAtUtcColumn { get; set; } = "consumed_at_utc";

    public string ExpiresAtUtcColumn { get; set; } = "expires_at_utc";

    public string CodeHashColumn { get; set; } = "code_hash";

    public string CodeColumn { get; set; } = "code";

    public string OriginColumn { get; set; } = "origin";
}

public sealed class SoftwareLicenseTableOptions
{
    public string TableName { get; set; } = "pc_sw_license";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string SoftwareNameColumn { get; set; } = "sw_name";

    public string LicenseSourceColumn { get; set; } = "license_source";

    public string UpdatedAtUtcColumn { get; set; } = "updated_at_utc";
}

public sealed class WorkerUpdatePinTableOptions
{
    public string TableName { get; set; } = "worker_update_pin";

    public string TargetServiceNameColumn { get; set; } = "target_service_name";

    public string VersionColumn { get; set; } = "version";

    public string PackageUrlColumn { get; set; } = "package_url";

    public string Sha256Column { get; set; } = "sha256";

    public string RequireAuthenticodeColumn { get; set; } = "require_authenticode";

    public string RollbackAfterMinutesColumn { get; set; } = "rollback_after_minutes";

    public string UpdatedAtUtcColumn { get; set; } = "updated_at_utc";
}

public sealed class UserMessageTableOptions
{
    public string TableName { get; set; } = "pc_user_message";

    public string PrimaryKeyColumn { get; set; } = "user_message_id";

    public string PcForeignKeyColumn { get; set; } = "pc_id";

    public string TitleColumn { get; set; } = "title";

    public string BodyColumn { get; set; } = "body";

    public string StatusColumn { get; set; } = "status";

    public string CreatedAtUtcColumn { get; set; } = "created_at_utc";

    public string ConsumedAtUtcColumn { get; set; } = "consumed_at_utc";
}

public static class UserMessageGrant
{
    public const string Pending = "pending";

    public const string Consumed = "consumed";

    public const int MaxTitleLength = 128;

    public const int MaxBodyLength = 1024;

    public const int PendingFlushLimit = 5;
}

public sealed class NotificationOptions
{
    public WebhookNotificationOptions Webhook { get; set; } = new();

    public SmtpNotificationOptions Smtp { get; set; } = new();

    public NotificationEventOptions Events { get; set; } = new();

    public TimeSpan StaleHeartbeatThreshold { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan StaleHeartbeatCheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    public bool HasEnabledChannel => Webhook.Enabled || Smtp.Enabled;
}

public sealed class WebhookNotificationOptions
{
    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class SmtpNotificationOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string[] AllowedCertificateThumbprints { get; set; } = [];

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public string[] Recipients { get; set; } = [];
}

public sealed class NotificationEventOptions
{
    public bool NewSoftware { get; set; } = true;

    public bool StaleHeartbeat { get; set; } = true;

    public bool BlacklistViolation { get; set; } = true;
}
