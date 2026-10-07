using SwLicenseWatcher.Setup.Core;

namespace SwLicenseWatcher.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            if (WindowsElevation.TryRelaunchIfNotElevated(
                    Environment.GetCommandLineArgs().Skip(1).ToArray(),
                    WindowsAdministratorPrivilege.Current,
                    WindowsElevatedProcessStarter.Instance,
                    Environment.ProcessPath,
                    out var elevatedExit))
            {
                Environment.ExitCode = elevatedExit;
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                SetupPaths.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();

        var arguments = SetupArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        var payloadDirectory = arguments.PayloadDirectory;
        if (string.IsNullOrWhiteSpace(payloadDirectory))
        {
            payloadDirectory = AppContext.BaseDirectory;
        }

        CompanySettings settings;
        try
        {
            settings = CompanySettingsStore.Load(PayloadLayout.GetCompanyJsonPath(payloadDirectory));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "company.json을 읽지 못했습니다.\n\n" + ex.Message,
                SetupPaths.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var setup = new AgentSetupOrchestrator(
            new WindowsAgentMachineIntegration(),
            privilege: WindowsAdministratorPrivilege.Current);
        if (arguments.Uninstall)
        {
            using var http = UninstallApiClient.Create(settings.ServerBaseUrl, settings.AgentToken);
            var uninstall = new UninstallOrchestrator(
                new UninstallApiClient(http),
                new InstalledDeviceCodeReader(),
                setup,
                new MldsaUninstallProofFactory());
            Application.Run(new UninstallForm(uninstall));
        }
        else
        {
            using var http = UninstallApiClient.Create(settings.ServerBaseUrl, settings.AgentToken);
            var upgrade = new InPlaceUpgradeAuthorizer(
                new UpgradeAuthorizationApiClient(http),
                new MldsaUpgradeProofFactory());
            Application.Run(new InstallForm(settings, payloadDirectory, arguments.SourceExePath, setup, upgrade));
        }
    }
}
