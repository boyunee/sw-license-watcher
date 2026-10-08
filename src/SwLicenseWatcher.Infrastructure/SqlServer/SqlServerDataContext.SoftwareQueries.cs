using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using SwLicenseWatcher.Application;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Infrastructure.SqlServer;

internal sealed partial class SqlServerDataContext
{
    public async Task<(int TotalCount, List<SoftwareAggregate> Items)> ListSoftwareAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var software = options.InstalledSoftwareTable;
        var sql = $"""
            SELECT
                COUNT(*) OVER() AS total_count,
                {Name(software.DisplayNameColumn)}, {Name(software.DisplayVersionColumn)},
                {Name(software.ClassificationColumn)},
                COUNT(DISTINCT {Name(software.PcForeignKeyColumn)}) AS device_count
            FROM {Name(options.SchemaName, software.TableName)}
            WHERE (@search IS NULL OR {Name(software.DisplayNameColumn)} LIKE @search)
              AND (@classification IS NULL OR {Name(software.ClassificationColumn)} = @classification)
            GROUP BY {Name(software.DisplayNameColumn)}, {Name(software.DisplayVersionColumn)}, {Name(software.ClassificationColumn)}
            ORDER BY {Name(software.DisplayNameColumn)}, {Name(software.DisplayVersionColumn)}, {Name(software.ClassificationColumn)}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@search", DbValue(ToContainsPattern(search))));
        command.Parameters.Add(new SqlParameter("@classification", DbValue(classification)));
        command.Parameters.Add(new SqlParameter("@skip", skip));
        command.Parameters.Add(new SqlParameter("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SoftwareAggregate>();
        var totalCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.Count == 0)
            {
                totalCount = reader.GetInt32(reader.GetOrdinal("total_count"));
            }

            items.Add(new SoftwareAggregate(
                reader.GetString(reader.GetOrdinal(software.DisplayNameColumn)),
                ReadNullableString(reader, software.DisplayVersionColumn),
                ReadClassification(reader, software.ClassificationColumn),
                reader.GetInt32(reader.GetOrdinal("device_count"))));
        }

        await reader.CloseAsync();
        if (items.Exists(item => item.Classification == SoftwarePolicyClassificationNames.Managed))
        {
            items = await FillSoftwareLicenseCountsAsync(connection, items, cancellationToken);
        }

        return (totalCount, items);
    }

    public async Task<(int TotalCount, List<SoftwareDevice> Items)> ListSoftwareDevicesAsync(
        string name,
        int skip,
        int take,
        string? classification,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var software = options.InstalledSoftwareTable;
        var pc = options.PcTable;
        var license = options.SoftwareLicenseTable;
        var sql = $"""
            SELECT
                COUNT(*) OVER() AS total_count,
                p.{Name(pc.DeviceCodeColumn)}, p.{Name(pc.HostNameColumn)}, p.{Name(pc.DomainNameColumn)},
                p.{Name(pc.OperatingSystemColumn)}, p.{Name(pc.AgentVersionColumn)},
                p.{Name(pc.LastHeartbeatUtcColumn)}, p.{Name(pc.LastInventoryUtcColumn)},
                s.{Name(software.DisplayVersionColumn)}, s.{Name(software.PublisherColumn)},
                s.{Name(software.ClassificationColumn)},
                l.{Name(license.LicenseSourceColumn)} AS license_source_override,
                p.{Name(pc.AssignedHostNameColumn)}, p.{Name(pc.AssignedDeviceCodeColumn)}
            FROM {Name(options.SchemaName, software.TableName)} AS s
            INNER JOIN {Name(options.SchemaName, pc.TableName)} AS p
                ON p.{Name(pc.PrimaryKeyColumn)} = s.{Name(software.PcForeignKeyColumn)}
            LEFT JOIN {Name(options.SchemaName, license.TableName)} AS l
                ON l.{Name(license.PcForeignKeyColumn)} = s.{Name(software.PcForeignKeyColumn)}
               AND l.{Name(license.SoftwareNameColumn)} = s.{Name(software.DisplayNameColumn)}
            WHERE s.{Name(software.DisplayNameColumn)} = @name
              AND (@classification IS NULL OR s.{Name(software.ClassificationColumn)} = @classification)
            ORDER BY {DisplayHostNameSql("p")}, p.{Name(pc.DeviceCodeColumn)}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@name", name));
        command.Parameters.Add(new SqlParameter("@classification", DbValue(classification)));
        command.Parameters.Add(new SqlParameter("@skip", skip));
        command.Parameters.Add(new SqlParameter("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SoftwareDevice>();
        var totalCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (items.Count == 0)
            {
                totalCount = reader.GetInt32(reader.GetOrdinal("total_count"));
            }

            var storedClassification = ReadClassification(reader, software.ClassificationColumn);
            var overrideSource = ReadNullableString(reader, "license_source_override");
            var storedCode = reader.GetString(reader.GetOrdinal(pc.DeviceCodeColumn));
            var assignedCode = ReadNullableString(reader, pc.AssignedDeviceCodeColumn);
            items.Add(new SoftwareDevice(
                DeviceCodes.Official(storedCode, assignedCode),
                reader.GetString(reader.GetOrdinal(pc.HostNameColumn)),
                reader.GetString(reader.GetOrdinal(pc.DomainNameColumn)),
                reader.GetString(reader.GetOrdinal(pc.OperatingSystemColumn)),
                reader.GetString(reader.GetOrdinal(pc.AgentVersionColumn)),
                ReadNullableDateTimeOffset(reader, pc.LastHeartbeatUtcColumn),
                ReadNullableDateTimeOffset(reader, pc.LastInventoryUtcColumn),
                ReadNullableString(reader, software.DisplayVersionColumn),
                ReadNullableString(reader, software.PublisherColumn),
                storedClassification,
                LicenseSource: null,
                LicenseSourceOverride: overrideSource,
                AssignedHostName: ReadNullableString(reader, pc.AssignedHostNameColumn)));
        }

        await reader.CloseAsync();
        if (items.Count > 0)
        {
            var policies = await ListEnabledPoliciesAsync(connection, transaction: null, cancellationToken);
            items = InventoryDecisions.ApplySoftwareDeviceLicenseSources(name, items, policies);
        }

        return (totalCount, items);
    }

    public async Task<(int TotalCount, List<SoftwareAggregateAssets> Items)> ListSoftwareWithAssetsAsync(
        int skip,
        int take,
        string? search,
        string? classification,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var software = options.InstalledSoftwareTable;
        var pc = options.PcTable;
        var name = Name(software.DisplayNameColumn);
        var version = Name(software.DisplayVersionColumn);
        var classificationColumn = Name(software.ClassificationColumn);
        // 페이지 그룹과 설치 PC를 SQL 안에서 짝지어 GROUP BY와 같은 비교 규칙(후행 공백, collation, NULL)을 그대로 쓴다.
        var sql = $"""
            WITH page AS (
                SELECT
                    COUNT(*) OVER() AS total_count,
                    ROW_NUMBER() OVER(ORDER BY {name}, {version}, {classificationColumn}) AS row_index,
                    {name}, {version}, {classificationColumn},
                    COUNT(DISTINCT {Name(software.PcForeignKeyColumn)}) AS device_count
                FROM {Name(options.SchemaName, software.TableName)}
                WHERE (@search IS NULL OR {name} LIKE @search)
                  AND (@classification IS NULL OR {classificationColumn} = @classification)
                GROUP BY {name}, {version}, {classificationColumn}
                ORDER BY {name}, {version}, {classificationColumn}
                OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            )
            SELECT page.total_count, page.row_index,
                page.{name}, page.{version}, page.{classificationColumn}, page.device_count,
                d.device_code, d.device_name
            FROM page
            OUTER APPLY (
                SELECT DISTINCT
                    {DisplayDeviceCodeSql("p")} AS device_code,
                    {DisplayHostNameSql("p")} AS device_name
                FROM {Name(options.SchemaName, software.TableName)} AS s
                INNER JOIN {Name(options.SchemaName, pc.TableName)} AS p
                    ON p.{Name(pc.PrimaryKeyColumn)} = s.{Name(software.PcForeignKeyColumn)}
                WHERE s.{name} = page.{name}
                  AND (s.{version} = page.{version} OR (s.{version} IS NULL AND page.{version} IS NULL))
                  AND (s.{classificationColumn} = page.{classificationColumn}
                       OR (s.{classificationColumn} IS NULL AND page.{classificationColumn} IS NULL))
                  AND (@search IS NULL OR s.{name} LIKE @search)
            ) AS d
            ORDER BY page.row_index, d.device_name, d.device_code;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@search", DbValue(ToContainsPattern(search))));
        command.Parameters.Add(new SqlParameter("@classification", DbValue(classification)));
        command.Parameters.Add(new SqlParameter("@skip", skip));
        command.Parameters.Add(new SqlParameter("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var aggregates = new List<SoftwareAggregate>();
        var devices = new List<List<SoftwareAssetDevice>>();
        var totalCount = 0;
        long? currentRow = null;
        var rowIndexOrdinal = reader.GetOrdinal("row_index");
        var deviceCodeOrdinal = reader.GetOrdinal("device_code");
        var deviceNameOrdinal = reader.GetOrdinal("device_name");
        while (await reader.ReadAsync(cancellationToken))
        {
            var rowIndex = reader.GetInt64(rowIndexOrdinal);
            if (rowIndex != currentRow)
            {
                if (aggregates.Count == 0)
                {
                    totalCount = reader.GetInt32(reader.GetOrdinal("total_count"));
                }

                currentRow = rowIndex;
                aggregates.Add(new SoftwareAggregate(
                    reader.GetString(reader.GetOrdinal(software.DisplayNameColumn)),
                    ReadNullableString(reader, software.DisplayVersionColumn),
                    ReadClassification(reader, software.ClassificationColumn),
                    reader.GetInt32(reader.GetOrdinal("device_count"))));
                devices.Add([]);
            }

            if (!reader.IsDBNull(deviceCodeOrdinal))
            {
                devices[^1].Add(new SoftwareAssetDevice(
                    reader.GetString(deviceCodeOrdinal),
                    reader.GetString(deviceNameOrdinal)));
            }
        }

        await reader.CloseAsync();
        if (aggregates.Exists(item => item.Classification == SoftwarePolicyClassificationNames.Managed))
        {
            aggregates = await FillSoftwareLicenseCountsAsync(connection, aggregates, cancellationToken);
        }

        return (totalCount, aggregates.Select((item, index) => new SoftwareAggregateAssets(item, devices[index])).ToList());
    }

    private async Task<List<SoftwareAggregate>> FillSoftwareLicenseCountsAsync(
        SqlConnection connection,
        List<SoftwareAggregate> items,
        CancellationToken cancellationToken)
    {
        var managedNames = items
            .Where(item => item.Classification == SoftwarePolicyClassificationNames.Managed)
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (managedNames.Count == 0)
        {
            return items;
        }

        var policies = await ListEnabledPoliciesAsync(connection, transaction: null, cancellationToken);
        var software = options.InstalledSoftwareTable;
        var license = options.SoftwareLicenseTable;
        var parameters = new List<SqlParameter>();
        var nameParams = new List<string>();
        for (var i = 0; i < managedNames.Count; i++)
        {
            var parameterName = "@name" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            nameParams.Add(parameterName);
            parameters.Add(new SqlParameter(parameterName, managedNames[i]));
        }

        var sql = $"""
            SELECT s.{Name(software.DisplayNameColumn)}, s.{Name(software.DisplayVersionColumn)},
                   s.{Name(software.PublisherColumn)}, s.{Name(software.PcForeignKeyColumn)},
                   l.{Name(license.LicenseSourceColumn)}
            FROM {Name(options.SchemaName, software.TableName)} AS s
            LEFT JOIN {Name(options.SchemaName, license.TableName)} AS l
                ON l.{Name(license.PcForeignKeyColumn)} = s.{Name(software.PcForeignKeyColumn)}
               AND l.{Name(license.SoftwareNameColumn)} = s.{Name(software.DisplayNameColumn)}
            WHERE s.{Name(software.ClassificationColumn)} = N'{SoftwarePolicyClassificationNames.Managed}'
              AND s.{Name(software.DisplayNameColumn)} IN ({string.Join(", ", nameParams)});
            """;
        var counts = new Dictionary<(string Name, string? Version), (int Company, int Byo, int Unassigned)>(
            SoftwareAggregateKeyComparer.Instance);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var version = reader.IsDBNull(1) ? null : reader.GetString(1);
            var publisher = reader.IsDBNull(2) ? null : reader.GetString(2);
            var assignment = reader.IsDBNull(4) ? null : reader.GetString(4);
            LicenseSourceNames.TryParse(assignment, out assignment);
            var match = SoftwarePolicyMatcher.Match(
                new InstalledSoftwareEntry(name, version, publisher, null, "query", "query"),
                policies);
            var policyDefault = match.Classification == SoftwarePolicyClassification.Managed
                ? match.Policy?.DefaultLicenseSource
                : null;
            var effective = LicenseSourceNames.ResolveEffective(
                assignment,
                SoftwarePolicyClassificationNames.Managed,
                policyDefault);
            var key = (name, version);
            counts.TryGetValue(key, out var current);
            counts[key] = effective switch
            {
                LicenseSourceNames.Company => (current.Company + 1, current.Byo, current.Unassigned),
                LicenseSourceNames.Byo => (current.Company, current.Byo + 1, current.Unassigned),
                _ => (current.Company, current.Byo, current.Unassigned + 1)
            };
        }

        return items.Select(item =>
        {
            if (item.Classification != SoftwarePolicyClassificationNames.Managed)
            {
                return item;
            }

            counts.TryGetValue((item.Name, item.Version), out var value);
            return item with { CompanyCount = value.Company, ByoCount = value.Byo, UnassignedCount = value.Unassigned };
        }).ToList();
    }

    private async Task<Dictionary<string, string>> ReadLicenseAssignmentsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        long pcId,
        CancellationToken cancellationToken)
    {
        var license = options.SoftwareLicenseTable;
        var sql = $"""
            SELECT {Name(license.SoftwareNameColumn)}, {Name(license.LicenseSourceColumn)}
            FROM {Name(options.SchemaName, license.TableName)}
            WHERE {Name(license.PcForeignKeyColumn)} = @pcId;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new SqlParameter("@pcId", pcId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            LicenseSourceNames.TryParse(reader.GetString(1), out var source);
            if (source is not null)
            {
                assignments[name] = source;
            }
        }

        return assignments;
    }

}
