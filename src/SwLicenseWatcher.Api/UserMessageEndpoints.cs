using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api;

internal static class UserMessageEndpoints
{
    internal static IEndpointRouteBuilder MapUserMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/inventory/devices/{deviceCode}/user-messages", async (
            string deviceCode,
            UserMessageWriteRequest request,
            IUserMessageStore store,
            UserMessageEventHub hub,
            CancellationToken cancellationToken) =>
        {
            if (!UninstallQueryApi.TryValidateDeviceCode(deviceCode, out var normalizedDeviceCode, out var deviceError))
            {
                return Results.BadRequest(deviceError);
            }

            if (!UserMessageText.TryNormalize(request.Title, request.Body, out var title, out var body, out var textError))
            {
                return Results.BadRequest(textError);
            }

            var enqueued = await store.EnqueueAsync(normalizedDeviceCode, title, body, cancellationToken);
            if (enqueued is null)
            {
                return Results.NotFound("The device is not registered.");
            }

            hub.Publish(enqueued.DeviceAliases, enqueued.Command);
            return Results.Created(
                $"/api/inventory/devices/{Uri.EscapeDataString(normalizedDeviceCode)}/user-messages/{enqueued.Command.Id}",
                new UserMessageCreatedResponse(
                    enqueued.Command.Id,
                    normalizedDeviceCode,
                    enqueued.Command.Title,
                    enqueued.Command.Body,
                    DateTimeOffset.UtcNow));
        });

        endpoints.MapPost("/api/inventory/user-messages/broadcast", async (
            UserMessageWriteRequest request,
            IUserMessageStore store,
            UserMessageEventHub hub,
            CancellationToken cancellationToken) =>
        {
            if (!UserMessageText.TryNormalize(request.Title, request.Body, out var title, out var body, out var textError))
            {
                return Results.BadRequest(textError);
            }

            var enqueued = await store.BroadcastAsync(title, body, cancellationToken);
            foreach (var item in enqueued)
            {
                hub.Publish(item.DeviceAliases, item.Command);
            }

            return Results.Ok(new UserMessageBroadcastResponse(enqueued.Count));
        });

        endpoints.MapPost("/api/agents/user-messages/{id:long}/consume", async (
            long id,
            UserMessageConsumeRequest request,
            IUserMessageStore store,
            DeviceEnrollmentService enrollment,
            CancellationToken cancellationToken) =>
        {
            if (!UninstallQueryApi.TryValidateDeviceCode(request.DeviceCode, out var deviceCode, out var deviceError))
            {
                return Results.BadRequest(deviceError);
            }

            var proofError = await enrollment.AuthorizeEnrolledAgentAsync(
                deviceCode,
                request.DeviceId,
                request.DeviceProof,
                cancellationToken);
            if (proofError is not null)
            {
                return Results.BadRequest(proofError);
            }

            return await store.ConsumeAsync(id, deviceCode, cancellationToken)
                ? Results.NoContent()
                : Results.Conflict("The user message is not valid.");
        });

        endpoints.MapGet(AgentPaths.Events, async (
            string? deviceCode,
            HttpContext http,
            IUserMessageStore store,
            UserMessageEventHub hub,
            DeviceEnrollmentService enrollment,
            CancellationToken cancellationToken) =>
        {
            if (!UninstallQueryApi.TryValidateDeviceCode(deviceCode, out var normalizedDeviceCode, out var error))
            {
                return Results.BadRequest(error);
            }

            var proofError = await enrollment.AuthorizeEnrolledAgentAsync(
                normalizedDeviceCode,
                DeviceProofHttp.ReadDeviceId(http.Request),
                DeviceProofHttp.ReadProof(http.Request),
                cancellationToken);
            if (proofError is not null)
            {
                return Results.BadRequest(proofError);
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache, no-store";
            http.Response.Headers.Pragma = "no-cache";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.StartAsync(cancellationToken);

            var pending = await store.ListPendingAsync(
                normalizedDeviceCode, UserMessageGrant.PendingFlushLimit, cancellationToken);
            foreach (var message in pending)
            {
                await WriteUserMessageAsync(http, message, cancellationToken);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, http.RequestAborted);
            var ct = linked.Token;
            await using var enumerator = hub.Subscribe(normalizedDeviceCode, ct).GetAsyncEnumerator(ct);
            try
            {
                var move = enumerator.MoveNextAsync().AsTask();
                while (!ct.IsCancellationRequested)
                {
                    var ping = Task.Delay(TimeSpan.FromSeconds(20), ct);
                    var completed = await Task.WhenAny(move, ping);
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (completed == ping && !move.IsCompleted)
                    {
                        await WritePingAsync(http, ct);
                        continue;
                    }

                    if (!await move)
                    {
                        break;
                    }

                    await WriteUserMessageAsync(http, enumerator.Current, ct);
                    move = enumerator.MoveNextAsync().AsTask();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }

            return Results.Empty;
        });

        return endpoints;
    }

    private static Task WriteUserMessageAsync(
        HttpContext http,
        AgentUserMessageCommand message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, InventoryJsonSerializerContext.Default.AgentUserMessageCommand);
        var payload = $"event: user-message\ndata: {json}\n\n";
        return http.Response.WriteAsync(payload, Encoding.UTF8, cancellationToken);
    }

    private static Task WritePingAsync(HttpContext http, CancellationToken cancellationToken) =>
        http.Response.WriteAsync(": ping\n\n", Encoding.UTF8, cancellationToken);
}
