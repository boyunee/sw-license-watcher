using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Infrastructure.SqlServer;

internal sealed class SqlServerSoftwareQueryRepository(SqlServerDataContext context) : ISoftwareQuery
{
    public Task<(int TotalCount, List<SoftwareAggregate> Items)> ListSoftwareAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken) =>
        context.ListSoftwareAsync(skip, take, search, classification, cancellationToken);

    public Task<(int TotalCount, List<SoftwareDevice> Items)> ListSoftwareDevicesAsync(
        string name,
        int skip,
        int take,
        string? classification,
        CancellationToken cancellationToken) =>
        context.ListSoftwareDevicesAsync(name, skip, take, classification, cancellationToken);

    public Task<(int TotalCount, List<SoftwareAggregateAssets> Items)> ListSoftwareWithAssetsAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken) =>
        context.ListSoftwareWithAssetsAsync(skip, take, search, classification, cancellationToken);
}
