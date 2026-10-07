using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Setup.Core;

public sealed class UninstallApiClient(HttpClient httpClient) : IUninstallApiClient
{
    public async Task<UninstallRequestCreated> CreateAsync(
        string deviceCode,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken)
    {
        var body = new UninstallRequestCreateBody
        {
            DeviceCode = deviceCode,
            DeviceId = deviceId,
            DeviceProof = proof
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(body, SetupJsonContext.Default.UninstallRequestCreateBody),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.PostAsync("/api/agents/uninstall-requests", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var created = await DeserializeAsync(response, SetupJsonContext.Default.UninstallRequestCreated, cancellationToken);
        if (created.Id <= 0)
        {
            throw new InvalidOperationException("The API did not return an uninstall request id.");
        }

        return created;
    }

    public async Task<AgentUninstallRequest> GetAsync(
        long requestId,
        string deviceCode,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken)
    {
        var path = $"/api/agents/uninstall-requests/{requestId}?deviceCode={Uri.EscapeDataString(deviceCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddProofHeaders(request, deviceId, proof);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await DeserializeAsync(response, SetupJsonContext.Default.AgentUninstallRequest, cancellationToken);
    }

    public async Task ConsumeAsync(
        long requestId,
        string deviceCode,
        string code,
        string? deviceId,
        string proof,
        CancellationToken cancellationToken)
    {
        var body = new UninstallRequestConsumeBody
        {
            DeviceCode = deviceCode,
            Code = code,
            DeviceId = deviceId,
            DeviceProof = proof
        };
        using var content = new StringContent(
            JsonSerializer.Serialize(body, SetupJsonContext.Default.UninstallRequestConsumeBody),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.PostAsync(
            $"/api/agents/uninstall-requests/{requestId}/consume",
            content,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public static HttpClient Create(string serverBaseUrl, string agentToken)
    {
        if (!CompanySettingsValidator.TryValidate(
                new CompanySettings
                {
                    ServerBaseUrl = serverBaseUrl,
                    AgentToken = agentToken,
                    Version = "x"
                },
                out var error))
        {
            throw new ArgumentException(error);
        }

        var client = new HttpClient
        {
            BaseAddress = new Uri(serverBaseUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentToken.Trim());
        return client;
    }

    private static void AddProofHeaders(HttpRequestMessage request, string? deviceId, string proof)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            request.Headers.TryAddWithoutValidation(DeviceProofHeaders.DeviceId, deviceId);
        }

        request.Headers.TryAddWithoutValidation(DeviceProofHeaders.DeviceProof, proof);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "This PC is not registered on the server yet. Install and wait for the first inventory snapshot, then try again."
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }

        if ((int)response.StatusCode == 409)
        {
            throw new InvalidOperationException(
                "The uninstall grant is no longer valid."
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }

        throw new InvalidOperationException($"The API returned HTTP {(int)response.StatusCode}. {detail}");
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
        if (value is null)
        {
            throw new InvalidOperationException("The API returned an empty response.");
        }

        return value;
    }
}
