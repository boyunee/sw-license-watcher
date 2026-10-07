namespace SwLicenseWatcher.Setup.Core;

public interface IDeviceUninstallProof
{
    bool TryCreate(string deviceCode, out string? deviceId, out string proof, out string error);
}

public interface IUninstallApiClient
{
    Task<UninstallRequestCreated> CreateAsync(
        string deviceCode,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken);

    Task<AgentUninstallRequest> GetAsync(
        long requestId,
        string deviceCode,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken);

    Task ConsumeAsync(
        long requestId,
        string deviceCode,
        string code,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken);
}
