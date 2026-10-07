namespace SwLicenseWatcher.Setup.Core;

public sealed record UninstallProgress(string Message);

public interface IUninstallWaiter
{
    DateTime UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemUninstallWaiter : IUninstallWaiter
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class UninstallOrchestrator
{
    private readonly IUninstallApiClient _api;
    private readonly InstalledDeviceCodeReader _deviceCodeReader;
    private readonly AgentSetupOrchestrator _setup;
    private readonly IDeviceUninstallProof _proof;
    private readonly IUninstallWaiter _waiter;

    public UninstallOrchestrator(
        IUninstallApiClient api,
        InstalledDeviceCodeReader deviceCodeReader,
        AgentSetupOrchestrator setup,
        IDeviceUninstallProof proof,
        IUninstallWaiter? waiter = null)
    {
        _api = api;
        _deviceCodeReader = deviceCodeReader;
        _setup = setup;
        _proof = proof;
        _waiter = waiter ?? new SystemUninstallWaiter();
    }

    public async Task RequestAndUninstallAsync(
        string machineName,
        IProgress<UninstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        _setup.EnsureCanChangeMachine();
        var deviceCode = _deviceCodeReader.Read(machineName);
        if (!_proof.TryCreate(deviceCode, out var deviceId, out var proof, out var proofError))
        {
            throw new InvalidOperationException(proofError);
        }

        progress?.Report(new UninstallProgress("제거 요청을 보내는 중입니다..."));
        var created = await _api.CreateAsync(deviceCode, deviceId, proof, cancellationToken);
        progress?.Report(new UninstallProgress($"관리자 승인 대기 중 (요청 {created.Id}, {deviceCode})..."));

        var deadline = _waiter.UtcNow.AddHours(2);
        while (_waiter.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _api.GetAsync(created.Id, deviceCode, deviceId, proof, cancellationToken);
            if (string.Equals(status.Status, "approved", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(status.Code))
                {
                    throw new InvalidOperationException("승인은 되었지만 해제 코드가 없습니다.");
                }

                await _api.ConsumeAsync(created.Id, deviceCode, status.Code, deviceId, proof, cancellationToken);
                _setup.Uninstall(removeState: false);
                return;
            }

            if (string.Equals(status.Status, "denied", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("제거 요청이 거절되었습니다. 서비스는 그대로입니다.");
            }

            if (string.Equals(status.Status, "expired", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Status, "consumed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"제거 권한이 {status.Status} 상태입니다. 서비스는 그대로입니다.");
            }

            await _waiter.DelayAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }

        throw new TimeoutException("관리자 승인 대기 시간이 지났습니다. 서비스는 그대로입니다.");
    }
}
