using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using SwLicenseWatcher.Core;
using SwLicenseWatcher.Crypto;

namespace SwLicenseWatcher.Agent.Worker;

public enum AgentPublishResult
{
    Succeeded,
    RetryableFailure,
    NonRetryableFailure
}

public sealed class AgentApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AgentApiClient> _logger;
    private readonly WorkerAgentOptions _options;

    private readonly AgentDeviceIdentityStore? _identity;

    public AgentApiClient(
        HttpClient httpClient,
        ILogger<AgentApiClient> logger,
        IOptions<WorkerAgentOptions> options,
        AgentDeviceIdentityStore? identity = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _identity = identity;
    }

    public Task<AgentPublishOutcome> PublishSnapshotAsync(InventoryIngestionRequest snapshot, CancellationToken cancellationToken) =>
        PostAsync(_options.SnapshotPath, snapshot, InventoryJsonSerializerContext.Default.InventoryIngestionRequest, cancellationToken);

    public Task<AgentPublishOutcome> PublishHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken) =>
        PostAsync(_options.HeartbeatPath, heartbeat, InventoryJsonSerializerContext.Default.AgentHeartbeat, cancellationToken);

    public async Task<bool> ConsumeUninstallAsync(
        long id,
        string deviceCode,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryCreateProof(deviceCode, out var deviceId, out var proof))
            {
                _logger.LogWarning("Device identity is not ready to consume uninstall grant {Id}.", id);
                return false;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/agents/uninstall-requests/{id}/consume")
            {
                Content = JsonContent.Create(
                    new UninstallRequestConsumeRequest(deviceCode, code, deviceId, proof),
                    InventoryJsonSerializerContext.Default.UninstallRequestConsumeRequest)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "POST uninstall consume {Id} returned {StatusCode}. Remote uninstall will not proceed.",
                id,
                (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to consume uninstall grant {Id}.", id);
            return false;
        }
    }

    public async Task<bool> ConsumeUserMessageAsync(
        long id,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryCreateProof(deviceCode, out var deviceId, out var proof))
            {
                _logger.LogWarning("Device identity is not ready to consume user message {Id}.", id);
                return false;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/agents/user-messages/{id}/consume")
            {
                Content = JsonContent.Create(
                    new UserMessageConsumeRequest(deviceCode, deviceId, proof),
                    InventoryJsonSerializerContext.Default.UserMessageConsumeRequest)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "POST user-message consume {Id} returned {StatusCode}.",
                id,
                (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to consume user message {Id}.", id);
            return false;
        }
    }

    private bool TryCreateProof(string deviceCode, out string? deviceId, out string proof)
    {
        deviceId = null;
        proof = string.Empty;
        if (_identity is null)
        {
            return false;
        }

        var stored = _identity.Current;
        if (string.IsNullOrWhiteSpace(stored.PrivateKey))
        {
            stored = _identity.Ensure();
        }

        deviceId = stored.DeviceId;
        if (!MldsaDeviceCrypto.TryFromBase64(stored.PrivateKey, out var privateKey))
        {
            return false;
        }

        proof = MldsaDeviceCrypto.ToBase64(
            MldsaDeviceCrypto.Sign(privateKey, DeviceProofs.Payload(stored.DeviceId ?? "", deviceCode)));
        return true;
    }

    private async Task<AgentPublishOutcome> PostAsync<TPayload>(string path, TPayload payload, JsonTypeInfo<TPayload> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(payload, typeInfo)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                AgentAssignmentStore.TryReadAssignment(
                    json,
                    out var assignmentSpecified,
                    out var assignedHostName,
                    out var deviceCodeSpecified,
                    out var assignedDeviceCode,
                    out var deviceId,
                    out var deviceCertificate);
                AgentAssignmentStore.TryReadUninstallCommand(json, out var uninstallCommand);
                AgentAssignmentStore.TryReadUserMessageCommand(json, out var userMessageCommand);
                return new AgentPublishOutcome(
                    AgentPublishResult.Succeeded,
                    assignmentSpecified,
                    assignedHostName,
                    deviceCodeSpecified,
                    assignedDeviceCode,
                    deviceId,
                    deviceCertificate,
                    uninstallCommand,
                    userMessageCommand);
            }

            var statusCode = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests || statusCode >= 500)
            {
                _logger.LogWarning(
                    "POST {Path} to {BaseAddress} returned {StatusCode}; the payload will be queued for retry.",
                    path,
                    _httpClient.BaseAddress,
                    statusCode);
                return new AgentPublishOutcome(AgentPublishResult.RetryableFailure, false, null);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "POST {Path} to {BaseAddress} returned {StatusCode}. Check Agent:ApiToken against API Security:AgentToken; the payload will not be queued for retry.",
                    path,
                    _httpClient.BaseAddress,
                    statusCode);
                return new AgentPublishOutcome(AgentPublishResult.NonRetryableFailure, false, null);
            }

            _logger.LogError(
                "POST {Path} to {BaseAddress} returned {StatusCode}. The payload was rejected and will not be queued for retry.",
                path,
                _httpClient.BaseAddress,
                statusCode);
            return new AgentPublishOutcome(AgentPublishResult.NonRetryableFailure, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Failed to POST {Path} to {BaseAddress}.", path, _httpClient.BaseAddress);
            return new AgentPublishOutcome(AgentPublishResult.RetryableFailure, false, null);
        }
    }
}
