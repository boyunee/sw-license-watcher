using System.Text.Json;
using SwLicenseWatcher.Core;
using SwLicenseWatcher.Crypto;

namespace SwLicenseWatcher.Agent.Worker;

internal static class DeviceProofCommand
{
    internal const string SwitchName = "--print-device-proof";
    internal const string DeviceCodeSwitch = "--device-code=";

    internal static bool TryHandle(string[] args)
    {
        if (!args.Any(argument => string.Equals(argument, SwitchName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
            var root = document.RootElement;
            var agent = root.GetProperty("Agent");
            var deviceCode = args
                .Select(argument => argument.StartsWith(DeviceCodeSwitch, StringComparison.OrdinalIgnoreCase)
                    ? argument[DeviceCodeSwitch.Length..]
                    : null)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? ReadString(agent, "DeviceCode");
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                throw new InvalidOperationException("Agent:DeviceCode is required to create a device proof.");
            }

            var healthPath = ReadString(agent, "HealthFilePath");
            var directory = string.IsNullOrWhiteSpace(healthPath) ? null : Path.GetDirectoryName(healthPath);
            var identityPath = string.IsNullOrEmpty(directory)
                ? "device-identity.bin"
                : Path.Combine(directory, "device-identity.bin");
            var localState = new LocalStateStoreOptions();
            if (root.TryGetProperty("LocalState", out var localStateElement))
            {
                var instanceName = ReadString(localStateElement, "InstanceName");
                var dpapiScope = ReadString(localStateElement, "DpapiScope");
                if (!string.IsNullOrWhiteSpace(instanceName))
                {
                    localState.InstanceName = instanceName;
                }

                if (!string.IsNullOrWhiteSpace(dpapiScope))
                {
                    localState.DpapiScope = dpapiScope;
                }
            }

            var store = new AgentDeviceIdentityStore(identityPath, new DpapiLocalStateProtector(localState));
            var stored = store.Current;
            if (!MldsaDeviceCrypto.TryFromBase64(stored.PrivateKey, out var privateKey))
            {
                throw new InvalidOperationException("The local device private key is not available.");
            }

            var proof = MldsaDeviceCrypto.ToBase64(
                MldsaDeviceCrypto.Sign(privateKey, DeviceProofs.Payload(stored.DeviceId ?? "", deviceCode)));
            Console.Out.WriteLine(stored.DeviceId ?? "");
            Console.Out.WriteLine(proof);
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
