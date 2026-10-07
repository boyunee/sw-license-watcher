using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api;

internal static class DeviceProofHttp
{
    internal static string? ReadDeviceId(HttpRequest request)
    {
        var value = request.Headers[DeviceProofHeaders.DeviceId].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static string? ReadProof(HttpRequest request)
    {
        var value = request.Headers[DeviceProofHeaders.DeviceProof].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal static class UninstallQueryApi
{
    public static void MapUninstallRequests(this WebApplication app)
    {
        app.MapPost("/api/agents/uninstall-requests", async (
            UninstallRequestCreateRequest request,
            IUninstallRequestStore repository,
            DeviceEnrollmentService enrollment,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidateDeviceCode(request.DeviceCode, out var deviceCode, out var error))
            {
                return Results.BadRequest(error);
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

            var created = await repository.CreateUninstallRequestAsync(deviceCode, cancellationToken);
            return created is null
                ? Results.NotFound("The device is not registered.")
                : Results.Created($"/api/agents/uninstall-requests/{created.Id}", created);
        });

        app.MapGet("/api/agents/uninstall-requests/{id:long}", async (
            long id,
            string? deviceCode,
            HttpContext http,
            IUninstallRequestStore repository,
            DeviceEnrollmentService enrollment,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidateDeviceCode(deviceCode, out var normalizedDeviceCode, out var error))
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

            var item = await repository.GetAgentUninstallRequestAsync(id, normalizedDeviceCode, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        app.MapPost("/api/agents/uninstall-requests/{id:long}/consume", async (
            long id,
            UninstallRequestConsumeRequest request,
            IUninstallRequestStore repository,
            DeviceEnrollmentService enrollment,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidateDeviceCode(request.DeviceCode, out var deviceCode, out var deviceError))
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

            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return Results.BadRequest("code is required.");
            }

            return await repository.ConsumeUninstallRequestAsync(id, deviceCode, request.Code, cancellationToken)
                ? Results.NoContent()
                : Results.Conflict("The uninstall grant is not valid.");
        });

        app.MapGet("/api/uninstall-requests", async (
            IUninstallRequestStore repository,
            int? skip,
            int? take,
            string? search,
            CancellationToken cancellationToken) =>
        {
            if (!QueryList.TryValidateSearch(search, out var searchError))
            {
                return Results.BadRequest(searchError);
            }

            var (normalizedSkip, normalizedTake) = QueryList.NormalizePaging(skip, take, csv: false);
            var (totalCount, items) = await repository.ListUninstallRequestsAsync(
                normalizedSkip, normalizedTake, search, cancellationToken);
            return Results.Ok(new UninstallRequestListResponse(normalizedSkip, normalizedTake, totalCount, items));
        });

        app.MapPost("/api/uninstall-requests/{id:long}/approve", async (
            long id,
            IUninstallRequestStore repository,
            CancellationToken cancellationToken) =>
            await repository.ApproveUninstallRequestAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.Conflict("The uninstall request is not pending."));

        app.MapPost("/api/uninstall-requests/{id:long}/deny", async (
            long id,
            IUninstallRequestStore repository,
            CancellationToken cancellationToken) =>
            await repository.DenyUninstallRequestAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.Conflict("The uninstall request is not pending."));

        app.MapPost("/api/uninstall-requests", async (
            UninstallRequestCreateRequest request,
            IUninstallRequestStore repository,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidateDeviceCode(request.DeviceCode, out var deviceCode, out var error))
            {
                return Results.BadRequest(error);
            }

            var created = await repository.CreateDirectedUninstallRequestAsync(deviceCode, cancellationToken);
            return created is null
                ? Results.NotFound("The device is not registered.")
                : Results.Created($"/api/uninstall-requests/{created.Id}", created);
        });

        app.MapPost("/api/uninstall-requests/{id:long}/cancel", async (
            long id,
            IUninstallRequestStore repository,
            CancellationToken cancellationToken) =>
            await repository.CancelDirectedUninstallRequestAsync(id, cancellationToken)
                ? Results.NoContent()
                : Results.Conflict("The directed uninstall grant is not active."));
    }

    internal static bool TryValidateDeviceCode(string? deviceCode, out string normalized, out string error)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            normalized = string.Empty;
            error = "deviceCode is required.";
            return false;
        }

        var trimmed = deviceCode.Trim();
        if (trimmed.Length > 128)
        {
            normalized = string.Empty;
            error = "deviceCode must be at most 128 characters.";
            return false;
        }

        normalized = trimmed;

        error = string.Empty;
        return true;
    }
}
