using SwLicenseWatcher.Setup.Core;

namespace SwLicenseWatcher.Setup.Tests;

public class UninstallOrchestratorTests
{
    [Fact]
    public async Task Approved_request_is_consumed_before_local_uninstall()
    {
        using var directory = new TempDirectory();
        var installRoot = Path.Combine(directory.Path, "install");
        var workerDirectory = SetupPaths.WorkerDirectory(installRoot);
        Directory.CreateDirectory(workerDirectory);
        File.WriteAllText(
            Path.Combine(workerDirectory, "appsettings.json"),
            """{"Agent":{"DeviceCode":"ASSET-7"}}""");
        var machine = new RecordingMachineIntegration();
        var api = new ApprovedApiClient(machine.Events);
        var setup = new AgentSetupOrchestrator(
            machine,
            installRoot: installRoot,
            stateRoot: Path.Combine(directory.Path, "state"),
            privilege: UnrestrictedAdministratorPrivilege.Instance);
        var sut = new UninstallOrchestrator(api, new InstalledDeviceCodeReader(installRoot), setup, new FixedDeviceProof());

        await sut.RequestAndUninstallAsync("MACHINE", progress: null, CancellationToken.None);

        Assert.Equal("ASSET-7", api.CreatedFor);
        Assert.Equal(
            [
                "create",
                "get",
                "consume",
                $"stop:{SetupPaths.WatchdogServiceName}",
                $"stop:{SetupPaths.WorkerServiceName}",
                $"delete:{SetupPaths.WatchdogServiceName}",
                $"delete:{SetupPaths.WorkerServiceName}",
                "delete-arp"
            ],
            machine.Events);
    }

    [Fact]
    public async Task Denied_request_leaves_local_install_untouched()
    {
        using var directory = new TempDirectory();
        var installRoot = Path.Combine(directory.Path, "install");
        Directory.CreateDirectory(SetupPaths.WorkerDirectory(installRoot));
        var marker = Path.Combine(SetupPaths.WorkerDirectory(installRoot), "marker");
        File.WriteAllText(marker, "installed");
        var machine = new RecordingMachineIntegration();
        var setup = new AgentSetupOrchestrator(
            machine,
            installRoot: installRoot,
            stateRoot: Path.Combine(directory.Path, "state"),
            privilege: UnrestrictedAdministratorPrivilege.Instance);
        var sut = new UninstallOrchestrator(
            new DeniedApiClient(),
            new InstalledDeviceCodeReader(installRoot),
            setup,
            new FixedDeviceProof());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RequestAndUninstallAsync("MACHINE", progress: null, CancellationToken.None));

        Assert.Equal("제거 요청이 거절되었습니다. 서비스는 그대로입니다.", error.Message);
        Assert.True(File.Exists(marker));
        Assert.Empty(machine.Events);
    }

    [Fact]
    public async Task Missing_administrator_privilege_stops_before_the_uninstall_request()
    {
        using var directory = new TempDirectory();
        var installRoot = Path.Combine(directory.Path, "install");
        Directory.CreateDirectory(SetupPaths.WorkerDirectory(installRoot));
        var machine = new RecordingMachineIntegration();
        var api = new MustNotBeCalledApiClient();
        var setup = new AgentSetupOrchestrator(
            machine,
            installRoot: installRoot,
            stateRoot: Path.Combine(directory.Path, "state"),
            privilege: new DeniedPrivilege());
        var sut = new UninstallOrchestrator(api, new InstalledDeviceCodeReader(installRoot), setup, new FixedDeviceProof());

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => sut.RequestAndUninstallAsync("MACHINE", progress: null, CancellationToken.None));

        Assert.Equal(WindowsAdministratorPrivilege.RequiredMessage, error.Message);
        Assert.Empty(machine.Events);
    }

    private sealed class ApprovedApiClient(List<string> events) : IUninstallApiClient
    {
        public string? CreatedFor { get; private set; }

        public Task<UninstallRequestCreated> CreateAsync(
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken)
        {
            CreatedFor = deviceCode;
            events.Add("create");
            return Task.FromResult(new UninstallRequestCreated { Id = 17, DeviceCode = deviceCode });
        }

        public Task<AgentUninstallRequest> GetAsync(
            long requestId,
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken)
        {
            events.Add("get");
            return Task.FromResult(new AgentUninstallRequest
            {
                Id = requestId,
                DeviceCode = deviceCode,
                Status = "approved",
                Code = "grant"
            });
        }

        public Task ConsumeAsync(
            long requestId,
            string deviceCode,
            string code,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken)
        {
            events.Add("consume");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedDeviceProof : IDeviceUninstallProof
    {
        public bool TryCreate(string deviceCode, out string? deviceId, out string proof, out string error)
        {
            deviceId = "device-1";
            proof = "proof";
            error = string.Empty;
            return true;
        }
    }

    private sealed class DeniedApiClient : IUninstallApiClient
    {
        public Task<UninstallRequestCreated> CreateAsync(
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UninstallRequestCreated { Id = 1, DeviceCode = deviceCode });

        public Task<AgentUninstallRequest> GetAsync(
            long requestId,
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentUninstallRequest { Id = requestId, DeviceCode = deviceCode, Status = "denied" });

        public Task ConsumeAsync(
            long requestId,
            string deviceCode,
            string code,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Consume should not be called.");
    }

    private sealed class RecordingMachineIntegration : IAgentMachineIntegration
    {
        public List<string> Events { get; } = [];

        public bool ServiceExists(string name) => false;

        public void StopService(string name) => Events.Add($"stop:{name}");

        public void InstallOrUpdateService(string name, string displayName, string description, string exePath) =>
            Events.Add($"install:{name}");

        public void StartService(string name) => Events.Add($"start:{name}");

        public void DeleteService(string name) => Events.Add($"delete:{name}");

        public void RegisterArp(string version, string setupExe) => Events.Add($"arp:{version}");

        public void DeleteArp() => Events.Add("delete-arp");
    }

    private sealed class DeniedPrivilege : IAdministratorPrivilege
    {
        public bool IsElevated => false;
    }

    private sealed class MustNotBeCalledApiClient : IUninstallApiClient
    {
        public Task<UninstallRequestCreated> CreateAsync(
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Uninstall API should not be called without elevation.");

        public Task<AgentUninstallRequest> GetAsync(
            long requestId,
            string deviceCode,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Uninstall API should not be called without elevation.");

        public Task ConsumeAsync(
            long requestId,
            string deviceCode,
            string code,
            string? deviceId,
            string proof,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Uninstall API should not be called without elevation.");
    }
}
