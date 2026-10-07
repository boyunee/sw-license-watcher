using System.Text;
using System.Text.Json.Serialization;

namespace SwLicenseWatcher.Core;

public static class DeviceIdentityAlgorithms
{
    // FIPS 204 highest parameter set (NIST Level 5). Slower than ML-DSA-44/65.
    public const string Mldsa87 = "ML-DSA-87";
}

public static class DeviceCodes
{
    public const int MaxLength = 128;

    public static string Official(string deviceCode, string? assignedDeviceCode) =>
        string.IsNullOrWhiteSpace(assignedDeviceCode) ? deviceCode : assignedDeviceCode.Trim();

    public static bool TryNormalizeAssigned(string? value, out string? normalized, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = null;
            error = string.Empty;
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            normalized = null;
            error = $"assignedDeviceCode must be at most {MaxLength} characters.";
            return false;
        }

        normalized = trimmed;
        error = string.Empty;
        return true;
    }
}

public static class DeviceProofHeaders
{
    public const string DeviceId = "X-Device-Id";

    public const string DeviceProof = "X-Device-Proof";
}

public static class DeviceProofs
{
    public static byte[] Payload(string deviceId, string deviceCode) =>
        Encoding.UTF8.GetBytes($"v1|{deviceId}|{deviceCode}");
}

public static class UserToastProofs
{
    public const string PublicKeyFileName = "device-public-key";

    public static TimeSpan MaxAge { get; } = TimeSpan.FromMinutes(2);

    public static TimeSpan ClockSkew { get; } = TimeSpan.FromSeconds(30);

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SwLicenseWatcher",
        "state");

    public static string DefaultPublicKeyPath { get; } = Path.Combine(DefaultDirectory, PublicKeyFileName);

    public static string PublicKeyPath(string identityDirectory) =>
        Path.Combine(identityDirectory, PublicKeyFileName);

    public static byte[] CanonicalBytes(long id, string title, string body, long issuedAtUnix) =>
        Encoding.UTF8.GetBytes($"v1|toast|{id}|{title}|{body}|{issuedAtUnix}\n");
}

public sealed record SignedUserToastPayload(
    long Id,
    string Title,
    string Body,
    long IssuedAtUnix,
    string Alg,
    string Signature);

public sealed record DeviceCertificateDocument(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pk")] string Pk,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("sig")] string Sig);

public sealed record DeviceAgentAssignment(
    string DeviceCode,
    string? AssignedHostName,
    string? DeviceId,
    string? DeviceCertificate);

public sealed record DeviceEnrollmentKeys(
    string DeviceCode,
    string? AssignedDeviceCode,
    string? DeviceId,
    string? DevicePublicKey,
    string? DeviceCertificate);

public sealed record DeviceUpgradeAuthorizationRequest(
    string DeviceCode,
    string? DeviceId = null,
    string? DevicePublicKey = null,
    string? DeviceCertificate = null,
    string? DeviceProof = null);

public sealed record DeviceUpgradeAuthorizationResponse(
    string DeviceCode,
    string? DeviceId,
    string? DevicePublicKey,
    string? DeviceCertificate);

public sealed record StoredDeviceIdentity(
    string? DeviceId,
    string PublicKey,
    string PrivateKey,
    string? Certificate);

public interface IDeviceCertificateAuthority
{
    DeviceCertificateDocument Issue(string deviceId, string publicKeyBase64);

    bool TryVerify(DeviceCertificateDocument certificate, out string error);
}

public enum DeviceProfileUpdateResult
{
    NotFound,
    Conflict,
    Updated
}
