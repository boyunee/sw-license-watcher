using System.Security.Cryptography;
using System.Text.Json;
using SwLicenseWatcher.Core;
using SwLicenseWatcher.Crypto;

namespace SwLicenseWatcher.Agent.Worker;

public sealed class AgentDeviceIdentityStore(string filePath, ILocalStateProtector protector)
{
    private readonly object _gate = new();
    private StoredDeviceIdentity _identity = Read(filePath, protector);

    public StoredDeviceIdentity Current
    {
        get
        {
            lock (_gate)
            {
                return _identity;
            }
        }
    }

    public StoredDeviceIdentity Ensure()
    {
        var generated = false;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_identity.PublicKey) || string.IsNullOrWhiteSpace(_identity.PrivateKey))
            {
                var (publicKey, privateKey) = MldsaDeviceCrypto.GenerateKeyPair();
                _identity = new StoredDeviceIdentity(
                    null,
                    MldsaDeviceCrypto.ToBase64(publicKey),
                    MldsaDeviceCrypto.ToBase64(privateKey),
                    null);
                generated = true;
            }
        }

        if (generated)
        {
            var written = _identity;
            try
            {
                Write();
            }
            catch
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_identity, written) || _identity.PrivateKey == written.PrivateKey)
                    {
                        _identity = new StoredDeviceIdentity(null, "", "", null);
                    }
                }

                throw;
            }
        }
        else
        {
            ExportPublicKey(Current);
        }

        return Current;
    }

    public void ApplyCertificate(string? deviceId, string? certificate)
    {
        if (string.IsNullOrWhiteSpace(deviceId) && string.IsNullOrWhiteSpace(certificate))
        {
            return;
        }

        StoredDeviceIdentity previous;
        lock (_gate)
        {
            if (string.Equals(_identity.DeviceId, deviceId, StringComparison.Ordinal) &&
                string.Equals(_identity.Certificate, certificate, StringComparison.Ordinal))
            {
                return;
            }

            previous = _identity;
            _identity = _identity with
            {
                DeviceId = string.IsNullOrWhiteSpace(deviceId) ? _identity.DeviceId : deviceId,
                Certificate = string.IsNullOrWhiteSpace(certificate) ? _identity.Certificate : certificate
            };
        }

        try
        {
            Write();
        }
        catch
        {
            lock (_gate)
            {
                _identity = previous;
            }

            throw;
        }
    }

    private void Write()
    {
        StoredDeviceIdentity identity;
        lock (_gate)
        {
            identity = _identity;
        }

        var json = JsonSerializer.Serialize(identity, InventoryJsonSerializerContext.Default.StoredDeviceIdentity);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = filePath + ".tmp";
        File.WriteAllText(temporaryPath, protector.Protect(json));
        File.Move(temporaryPath, filePath, true);
        if (!LocalIdentityFileAcl.RestrictPrivateKey(filePath))
        {
            TryDelete(filePath);
            TryDelete(temporaryPath);
            throw new InvalidOperationException(
                "The device private key file could not be restricted to Administrators and SYSTEM.");
        }

        ExportPublicKey(identity);
    }

    private void ExportPublicKey(StoredDeviceIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.PublicKey))
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var publicKeyPath = UserToastProofs.PublicKeyPath(directory);
        var temporaryPath = publicKeyPath + ".tmp";
        File.WriteAllText(temporaryPath, identity.PublicKey.Trim());
        File.Move(temporaryPath, publicKeyPath, true);
        LocalIdentityFileAcl.AllowUsersRead(publicKeyPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static StoredDeviceIdentity Read(string path, ILocalStateProtector protector)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new StoredDeviceIdentity(null, "", "", null);
            }

            var json = protector.Unprotect(File.ReadAllText(path));
            return JsonSerializer.Deserialize(json, InventoryJsonSerializerContext.Default.StoredDeviceIdentity)
                ?? new StoredDeviceIdentity(null, "", "", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException or FormatException)
        {
            return new StoredDeviceIdentity(null, "", "", null);
        }
    }
}
