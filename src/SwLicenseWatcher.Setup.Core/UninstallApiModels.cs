namespace SwLicenseWatcher.Setup.Core;

public sealed class UninstallRequestCreateBody
{
    public string DeviceCode { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string? DeviceProof { get; set; }
}

public sealed class UninstallRequestCreated
{
    public long Id { get; set; }

    public string DeviceCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

public sealed class AgentUninstallRequest
{
    public long Id { get; set; }

    public string DeviceCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Code { get; set; }
}

public sealed class UninstallRequestConsumeBody
{
    public string DeviceCode { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string? DeviceId { get; set; }

    public string? DeviceProof { get; set; }
}
