using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api;

internal static class InventoryQueryApi
{
    internal const int DefaultTake = QueryList.DefaultTake;
    internal const int CsvDefaultTake = QueryList.CsvDefaultTake;
    internal const int MaxTake = QueryList.MaxTake;
    internal const int MaxSearchLength = QueryList.MaxSearchLength;

    public static void MapInventoryQuery(this WebApplication app)
    {
        app.MapGet("/api/inventory/devices", async (
            IDeviceQuery repository,
            int? skip,
            int? take,
            string? search,
            int? staleAfterHours,
            string? format,
            CancellationToken cancellationToken) =>
        {
            if (staleAfterHours is < 1)
            {
                return Results.BadRequest("staleAfterHours must be a positive integer.");
            }

            if (!QueryList.TryValidateSearch(search, out var searchError))
            {
                return Results.BadRequest(searchError);
            }

            var csv = QueryList.WantsCsv(format);
            var (normalizedSkip, normalizedTake) = QueryList.NormalizePaging(skip, take, csv);
            var (totalCount, items) = await repository.ListDevicesAsync(
                normalizedSkip, normalizedTake, search, staleAfterHours, cancellationToken);

            if (csv)
            {
                return InventoryCsv.File(
                    "devices.csv",
                    ["DeviceCode", "HostName", "AssignedHostName", "AdminNotes", "DomainName", "OperatingSystem", "AgentVersion", "LastHeartbeatUtc", "LastInventoryUtc"],
                    items.Select(device => new[]
                    {
                        device.DeviceCode,
                        device.HostName,
                        device.AssignedHostName,
                        device.AdminNotes,
                        device.DomainName,
                        device.OperatingSystem,
                        device.AgentVersion,
                        InventoryCsv.Format(device.LastHeartbeatUtc),
                        InventoryCsv.Format(device.LastInventoryUtc)
                    }));
            }

            return Results.Ok(new DeviceListResponse(normalizedSkip, normalizedTake, totalCount, items));
        });

        app.MapGet("/api/inventory/devices/{deviceCode}", GetDeviceAsync);
        app.MapGet("/api/inventory/snapshots/{deviceCode}", GetDeviceAsync);

        app.MapGet("/api/inventory/software", async (
            ISoftwareQuery repository,
            int? skip,
            int? take,
            string? search,
            string? classification,
            string? format,
            CancellationToken cancellationToken) =>
        {
            if (!QueryList.TryValidateSearch(search, out var searchError))
            {
                return Results.BadRequest(searchError);
            }

            if (!TryNormalizeClassification(classification, out var normalizedClassification, out var classificationError))
            {
                return Results.BadRequest(classificationError);
            }

            var csv = QueryList.WantsCsv(format);
            var (normalizedSkip, normalizedTake) = QueryList.NormalizePaging(skip, take, csv);
            if (csv)
            {
                var (_, rows) = await repository.ListSoftwareWithAssetsAsync(
                    normalizedSkip, normalizedTake, search, normalizedClassification, cancellationToken);
                return InventoryCsv.File(
                    "software.csv",
                    ["Name", "Version", "Classification", "DeviceCount", "CompanyCount", "ByoCount", "UnassignedCount", "DeviceCodes", "DeviceNames"],
                    rows.Select(SoftwareCsvRow));
            }

            var (totalCount, items) = await repository.ListSoftwareAsync(
                normalizedSkip, normalizedTake, search, normalizedClassification, cancellationToken);
            return Results.Ok(new SoftwareAggregateListResponse(normalizedSkip, normalizedTake, totalCount, items));
        });

        app.MapGet("/api/inventory/software/{name}/devices", async (
            string name,
            ISoftwareQuery repository,
            int? skip,
            int? take,
            string? classification,
            string? format,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest("Software name is required.");
            }

            if (name.Length > MaxSearchLength)
            {
                return Results.BadRequest($"Software name must be at most {MaxSearchLength} characters.");
            }

            if (!TryNormalizeClassification(classification, out var normalizedClassification, out var classificationError))
            {
                return Results.BadRequest(classificationError);
            }

            var csv = QueryList.WantsCsv(format);
            var (normalizedSkip, normalizedTake) = QueryList.NormalizePaging(skip, take, csv);
            var (totalCount, items) = await repository.ListSoftwareDevicesAsync(
                name, normalizedSkip, normalizedTake, normalizedClassification, cancellationToken);

            if (csv)
            {
                return InventoryCsv.File(
                    $"software-{InventoryCsv.SafeFileName(name)}-devices.csv",
                    ["Name", "DeviceCode", "HostName", "AssignedHostName", "DomainName", "OperatingSystem", "AgentVersion", "LastHeartbeatUtc", "LastInventoryUtc", "Version", "Publisher", "Classification", "LicenseSource", "LicenseSourceOverride"],
                    items.Select(device => new[]
                    {
                        name,
                        device.DeviceCode,
                        device.HostName,
                        device.AssignedHostName,
                        device.DomainName,
                        device.OperatingSystem,
                        device.AgentVersion,
                        InventoryCsv.Format(device.LastHeartbeatUtc),
                        InventoryCsv.Format(device.LastInventoryUtc),
                        device.Version,
                        device.Publisher,
                        device.Classification,
                        device.LicenseSource,
                        device.LicenseSourceOverride
                    }));
            }

            return Results.Ok(new SoftwareDeviceListResponse(name, normalizedSkip, normalizedTake, totalCount, items));
        });

        app.MapPut("/api/inventory/software/classifications", async (
            SoftwareClassificationBatchWriteRequest request,
            IPolicyStore repository,
            CancellationToken cancellationToken) =>
        {
            if (!SoftwarePolicyValidator.TryValidateClassificationBatch(request, out var items, out var validationError))
            {
                return Results.BadRequest(validationError);
            }

            var saved = await repository.UpsertSoftwareClassificationsAsync(items, cancellationToken);
            return Results.Ok(new SoftwareClassificationBatchResponse(saved.Count, saved));
        });

        app.MapPut("/api/inventory/software/{name}/classification", async (
            string name,
            SoftwareClassificationWriteRequest request,
            IPolicyStore repository,
            CancellationToken cancellationToken) =>
        {
            if (!SoftwarePolicyValidator.TryValidateSoftwareName(name, out var productName, out var nameError))
            {
                return Results.BadRequest(nameError);
            }

            if (!SoftwarePolicyValidator.TryValidateClassification(request, out var validationError))
            {
                return Results.BadRequest(validationError);
            }

            var saved = await repository.UpsertSoftwareClassificationAsync(productName, request, cancellationToken);
            return Results.Ok(saved);
        });

        app.MapPut("/api/inventory/devices/{deviceCode}/software/{name}/license-source", async (
            string deviceCode,
            string name,
            DeviceSoftwareLicenseSourceWriteRequest request,
            IPolicyStore repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                return Results.BadRequest("deviceCode is required.");
            }

            if (!SoftwarePolicyValidator.TryValidateSoftwareName(name, out var softwareName, out var nameError))
            {
                return Results.BadRequest(nameError);
            }

            if (!TryNormalizeLicenseSource(request.LicenseSource, out var licenseSource, out var sourceError))
            {
                return Results.BadRequest(sourceError);
            }

            return await repository.SetDeviceSoftwareLicenseSourceAsync(
                deviceCode, softwareName, licenseSource, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        app.MapPut("/api/inventory/devices/{deviceCode}", async (
            string deviceCode,
            DeviceProfileWriteRequest request,
            IDeviceQuery repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                return Results.BadRequest("deviceCode is required.");
            }

            if (request is null)
            {
                return Results.BadRequest("The device profile payload is required.");
            }

            if (!PcDisplayNames.TryNormalizeAssignedHostName(request.AssignedHostName, out var assignedHostName, out var nameError))
            {
                return Results.BadRequest(nameError);
            }

            if (!PcDisplayNames.TryNormalizeAdminNotes(request.AdminNotes, out var adminNotes, out var notesError))
            {
                return Results.BadRequest(notesError);
            }

            if (!DeviceCodes.TryNormalizeAssigned(request.AssignedDeviceCode, out var assignedDeviceCode, out var codeError))
            {
                return Results.BadRequest(codeError);
            }

            return await repository.UpdateDeviceProfileAsync(
                deviceCode,
                new DeviceProfileWriteRequest(assignedHostName, adminNotes, assignedDeviceCode),
                cancellationToken) switch
            {
                DeviceProfileUpdateResult.Updated => Results.NoContent(),
                DeviceProfileUpdateResult.Conflict => Results.Conflict("assignedDeviceCode is already used by another PC."),
                _ => Results.NotFound()
            };
        });
    }

    private static async Task<IResult> GetDeviceAsync(
        string deviceCode,
        IDeviceQuery repository,
        string? classification,
        string? format,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return Results.BadRequest("deviceCode is required.");
        }

        if (!TryNormalizeClassification(classification, out var normalizedClassification, out var classificationError))
        {
            return Results.BadRequest(classificationError);
        }

        var detail = await repository.GetDeviceAsync(deviceCode, normalizedClassification, cancellationToken);
        if (detail is null)
        {
            return Results.NotFound();
        }

        if (QueryList.WantsCsv(format))
        {
            IEnumerable<string?[]> rows = detail.InstalledSoftware.Count == 0
                ? [DeviceSoftwareRow(detail, null)]
                : detail.InstalledSoftware.Select(entry => DeviceSoftwareRow(detail, entry));
            return InventoryCsv.File(
                $"device-{InventoryCsv.SafeFileName(detail.DeviceCode)}.csv",
                ["DeviceCode", "HostName", "AssignedHostName", "AdminNotes", "DomainName", "OperatingSystem", "AgentVersion", "LastHeartbeatUtc", "LastInventoryUtc", "Name", "Version", "Publisher", "InstallLocation", "DiscoveryScope", "DiscoverySource", "Classification", "LicenseSource", "LicenseSourceOverride"],
                rows);
        }

        return Results.Ok(detail);
    }

    private static string?[] DeviceSoftwareRow(DeviceDetail detail, InstalledSoftwareEntry? entry) =>
    [
        detail.DeviceCode,
        detail.HostName,
        detail.AssignedHostName,
        detail.AdminNotes,
        detail.DomainName,
        detail.OperatingSystem,
        detail.AgentVersion,
        InventoryCsv.Format(detail.LastHeartbeatUtc),
        InventoryCsv.Format(detail.LastInventoryUtc),
        entry?.Name,
        entry?.Version,
        entry?.Publisher,
        entry?.InstallLocation,
        entry?.DiscoveryScope,
        entry?.DiscoverySource,
        entry?.Classification,
        entry?.LicenseSource,
        entry?.LicenseSourceOverride
    ];

    internal static string?[] SoftwareCsvRow(SoftwareAggregateAssets row) =>
    [
        row.Software.Name,
        row.Software.Version,
        row.Software.Classification,
        row.Software.DeviceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        row.Software.CompanyCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        row.Software.ByoCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        row.Software.UnassignedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string.Join("; ", row.Devices.Select(device => device.DeviceCode)),
        string.Join("; ", row.Devices.Select(device => device.DeviceName))
    ];

    internal static bool TryNormalizeClassification(string? classification, out string? normalized, out string error)
    {
        if (string.IsNullOrWhiteSpace(classification))
        {
            normalized = null;
            error = string.Empty;
            return true;
        }

        if (!SoftwarePolicyClassificationNames.TryParseInstalledSoftware(classification, out var storage))
        {
            normalized = null;
            error = "classification must be white, managed, black, or unclassified.";
            return false;
        }

        normalized = storage;
        error = string.Empty;
        return true;
    }

    internal static bool TryNormalizeLicenseSource(string? licenseSource, out string? normalized, out string error)
    {
        if (!LicenseSourceNames.TryParse(licenseSource, out normalized))
        {
            error = "licenseSource must be company or byo.";
            return false;
        }

        error = string.Empty;
        return true;
    }

}
