using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using SwLicenseWatcher.Core;
using SwLicenseWatcher.Crypto;

namespace SwLicenseWatcher.Agent.Worker;

public sealed class AgentEventStreamClient(
    HttpClient httpClient,
    IOptions<WorkerAgentOptions> options,
    AgentAssignmentStore assignmentStore,
    AgentDeviceIdentityStore identityStore,
    ILogger<AgentEventStreamClient> logger)
{
    public async Task ListenAsync(
        Func<AgentUserMessageCommand, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        var deviceCode = assignmentStore.ResolveDeviceCode(options.Value.DeviceCode);
        var stored = identityStore.Ensure();
        if (!MldsaDeviceCrypto.TryFromBase64(stored.PrivateKey, out var privateKey))
        {
            logger.LogWarning("Device identity is not ready for the user-message event stream.");
            return;
        }

        var proof = MldsaDeviceCrypto.ToBase64(
            MldsaDeviceCrypto.Sign(privateKey, DeviceProofs.Payload(stored.DeviceId ?? "", deviceCode)));
        var path = $"{options.Value.EventsPath}?deviceCode={Uri.EscapeDataString(deviceCode)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiToken);
        if (!string.IsNullOrWhiteSpace(stored.DeviceId))
        {
            request.Headers.TryAddWithoutValidation(DeviceProofHeaders.DeviceId, stored.DeviceId);
        }

        request.Headers.TryAddWithoutValidation(DeviceProofHeaders.DeviceProof, proof);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var sse in SseEventReader.ReadAsync(stream, cancellationToken))
        {
            if (!SseEventReader.TryParseUserMessage(sse, out var command) || command is null)
            {
                continue;
            }

            await onMessage(command, cancellationToken);
        }

        logger.LogInformation("User-message event stream closed for {DeviceCode}.", deviceCode);
    }
}

public sealed class AgentEventStreamService(
    AgentEventStreamClient client,
    UserMessageDelivery delivery,
    IOptions<WorkerAgentOptions> options,
    ILogger<AgentEventStreamService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.RunOnceForDiagnostics)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.ListenAsync(delivery.DeliverAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "User-message event stream failed; reconnecting.");
            }

            try
            {
                await Task.Delay(
                    JitterDelayCalculator.NextDelay(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(55)),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
