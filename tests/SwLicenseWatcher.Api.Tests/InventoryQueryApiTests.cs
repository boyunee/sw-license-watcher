using System.Text.Json;
using SwLicenseWatcher.Api;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api.Tests;

public class InventoryQueryApiTests
{
    [Fact]
    public void SoftwareCsvRow_appends_device_codes_and_names()
    {
        var row = InventoryQueryApi.SoftwareCsvRow(new SoftwareAggregateAssets(
            new SoftwareAggregate("Chrome", "120", "managed", 2, CompanyCount: 1, ByoCount: 1),
            [new SoftwareAssetDevice("A001", "홍길동PC"), new SoftwareAssetDevice("A002", "PC-02")]));

        Assert.Equal(
            ["Chrome", "120", "managed", "2", "1", "1", "0", "A001; A002", "홍길동PC; PC-02"],
            row);
    }

    [Fact]
    public void SoftwareCsvRow_leaves_device_columns_empty_without_devices()
    {
        var row = InventoryQueryApi.SoftwareCsvRow(new SoftwareAggregateAssets(
            new SoftwareAggregate("Chrome", null, "white", 0), []));

        Assert.Equal(string.Empty, row[7]);
        Assert.Equal(string.Empty, row[8]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TryNormalizeClassification_allows_missing_filter(string? classification)
    {
        Assert.True(InventoryQueryApi.TryNormalizeClassification(classification, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("unclassified", "unclassified")]
    [InlineData("WHITE", "white")]
    [InlineData("whitelist", "white")]
    [InlineData("managed", "managed")]
    [InlineData("black", "black")]
    [InlineData("Blacklist", "black")]
    public void TryNormalizeClassification_accepts_known_values(string classification, string expected)
    {
        Assert.True(InventoryQueryApi.TryNormalizeClassification(classification, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("grey")]
    public void TryNormalizeClassification_rejects_invalid_values(string classification)
    {
        Assert.False(InventoryQueryApi.TryNormalizeClassification(classification, out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Equal("classification must be white, managed, black, or unclassified.", error);
    }

    [Fact]
    public void Software_query_dtos_serialize_classification_with_source_generator()
    {
        var aggregate = new SoftwareAggregate("Chrome", "120.0", "managed", 3, 1, 1, 1);
        var aggregateJson = JsonSerializer.Serialize(aggregate, ApiJsonSerializerContext.Default.SoftwareAggregate);
        Assert.Contains("\"Classification\":\"managed\"", aggregateJson, StringComparison.Ordinal);
        Assert.Contains("\"CompanyCount\":1", aggregateJson, StringComparison.Ordinal);
        Assert.Contains("\"ByoCount\":1", aggregateJson, StringComparison.Ordinal);
        Assert.Contains("\"UnassignedCount\":1", aggregateJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", aggregateJson, StringComparison.Ordinal);

        var device = new SoftwareDevice("PC-01", "host", "WORKGROUP", "Windows", "1.0.0", null, null, "120.0", "Google", "managed", "company", null);
        var deviceJson = JsonSerializer.Serialize(device, ApiJsonSerializerContext.Default.SoftwareDevice);
        Assert.Contains("\"Classification\":\"managed\"", deviceJson, StringComparison.Ordinal);
        Assert.Contains("\"LicenseSource\":\"company\"", deviceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", deviceJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("company", "company")]
    [InlineData("BYO", "byo")]
    public void TryNormalizeLicenseSource_accepts_company_byo_or_blank(string? value, string? expected)
    {
        Assert.True(InventoryQueryApi.TryNormalizeLicenseSource(value, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Classification_batch_dtos_serialize_without_tokens()
    {
        var request = new SoftwareClassificationBatchWriteRequest(
        [
            new SoftwareClassificationItemWriteRequest("Chrome", SoftwarePolicyClassification.Managed, "Google", "company")
        ]);
        var requestJson = JsonSerializer.Serialize(request, ApiJsonSerializerContext.Default.SoftwareClassificationBatchWriteRequest);
        Assert.Contains("\"Name\":\"Chrome\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"Classification\":\"managed\"", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", requestJson, StringComparison.Ordinal);

        var response = new SoftwareClassificationBatchResponse(
            1,
            [
                new SoftwarePolicyEntry(1, "Chrome", "Google", null, SoftwarePolicyClassification.Managed, null, true, DateTimeOffset.UnixEpoch, "company")
            ]);
        var responseJson = JsonSerializer.Serialize(response, ApiJsonSerializerContext.Default.SoftwareClassificationBatchResponse);
        Assert.Contains("\"UpdatedCount\":1", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Token", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TryNormalizeLicenseSource_rejects_unknown_values()
    {
        Assert.False(InventoryQueryApi.TryNormalizeLicenseSource("personal", out var normalized, out var error));
        Assert.Null(normalized);
        Assert.Equal("licenseSource must be company or byo.", error);
    }
}
