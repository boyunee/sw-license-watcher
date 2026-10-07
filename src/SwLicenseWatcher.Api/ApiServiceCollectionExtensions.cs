using Microsoft.Extensions.Options;
using SwLicenseWatcher.Core;
using SwLicenseWatcher.Crypto;
using SwLicenseWatcher.Infrastructure;

namespace SwLicenseWatcher.Api;

internal static class ApiServiceCollectionExtensions
{
    internal static IServiceCollection AddApiOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SqlServerStorageOptions>()
            .Bind(configuration.GetSection("Storage:SqlServer"))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                "Storage:SqlServer:ConnectionString is required.")
            .Validate(options =>
            {
                try
                {
                    SqlIdentifierValidator.Validate(options);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }, "All SQL identifiers must use letters, digits, and underscores, start with a letter or underscore, and be at most 128 characters.")
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("Database"));
        services.AddOptions<ApiSecurityOptions>()
            .Bind(configuration.GetSection("Security"))
            .Validate(
                ApiSecurityOptionsValidator.HasRequiredRoleTokens,
                "Security:AgentToken and Security:AdminToken must each contain at least 32 characters.")
            .Validate(
                ApiSecurityOptionsValidator.HasValidConfiguredTokenLengths,
                "Every configured Security token (AgentToken, AdminToken) must contain at least 32 characters.")
            .Validate(
                ApiSecurityOptionsValidator.RejectsLegacySharedToken,
                "Security:Token is no longer accepted. Configure distinct Security:AgentToken and Security:AdminToken.")
            .Validate(
                ApiSecurityOptionsValidator.HasDistinctRoleTokens,
                "Security:AgentToken must differ from Security:AdminToken.")
            .Validate(
                ApiSecurityOptionsValidator.HasValidJwt,
                "Security:Jwt:Authority requires an https Authority, Security:Jwt:Audience, and Security:Jwt:RequiredScope or Security:Jwt:RequiredRole. MetadataAddress, when set, must be https. ClockSkew must not be negative.")
            .ValidateOnStart();
        services.AddOptions<UpdateManifestOptions>()
            .Bind(configuration.GetSection("Updates:Worker"))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PackageUrl),
                "Updates:Worker:PackageUrl is required.")
            .Validate(
                options => Uri.TryCreate(options.PackageUrl, UriKind.Absolute, out _),
                "Updates:Worker:PackageUrl must be an absolute URI.")
            .ValidateOnStart();
        services.AddOptions<GitHubUpdateOptions>()
            .Bind(configuration.GetSection("Updates:GitHub"))
            .Validate(
                options => options.MaxPackageBytes > 0,
                "Updates:GitHub:MaxPackageBytes must be positive.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PackageDirectory),
                "Updates:GitHub:PackageDirectory is required.")
            .Validate(
                options => options.Timeout > TimeSpan.Zero,
                "Updates:GitHub:Timeout must be positive.")
            .Validate(
                options => options.ReleaseListLimit is >= 1 and <= 100,
                "Updates:GitHub:ReleaseListLimit must be between 1 and 100.")
            .Validate(
                options => string.IsNullOrWhiteSpace(options.Owner) == string.IsNullOrWhiteSpace(options.Repository),
                "Updates:GitHub:Owner and Updates:GitHub:Repository must both be set, or both left empty.")
            .ValidateOnStart();
        services.AddOptions<DeviceEnrollmentOptions>()
            .Bind(configuration.GetSection("DeviceEnrollment"));
        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection("Notifications"))
            .Validate(
                options => !options.Webhook.Enabled
                    || (Uri.TryCreate(options.Webhook.Url, UriKind.Absolute, out var webhookUri)
                        && (webhookUri.Scheme == Uri.UriSchemeHttps || webhookUri.Scheme == Uri.UriSchemeHttp)),
                "Notifications:Webhook:Url must be an absolute HTTP or HTTPS URI when webhook notifications are enabled.")
            .Validate(
                options => !options.Webhook.Enabled || options.Webhook.Timeout > TimeSpan.Zero,
                "Notifications:Webhook:Timeout must be positive when webhook notifications are enabled.")
            .Validate(
                options => !options.Smtp.Enabled || !string.IsNullOrWhiteSpace(options.Smtp.Host),
                "Notifications:Smtp:Host is required when SMTP notifications are enabled.")
            .Validate(
                options => !options.Smtp.Enabled || options.Smtp.Port is >= 1 and <= 65535,
                "Notifications:Smtp:Port must be between 1 and 65535 when SMTP notifications are enabled.")
            .Validate(
                options => !options.Smtp.Enabled || !string.IsNullOrWhiteSpace(options.Smtp.From),
                "Notifications:Smtp:From is required when SMTP notifications are enabled.")
            .Validate(
                options => !options.Smtp.Enabled || options.Smtp.Recipients.Any(recipient => !string.IsNullOrWhiteSpace(recipient)),
                "Notifications:Smtp:Recipients must contain at least one address when SMTP notifications are enabled.")
            .Validate(
                options => options.StaleHeartbeatThreshold > TimeSpan.Zero,
                "Notifications:StaleHeartbeatThreshold must be positive.")
            .Validate(
                options => options.StaleHeartbeatCheckInterval > TimeSpan.Zero,
                "Notifications:StaleHeartbeatCheckInterval must be positive.")
            .ValidateOnStart();

        return services;
    }

    internal static IServiceCollection AddApiServices(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonSerializerContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, InventoryJsonSerializerContext.Default);
        });
        services.AddSingleton<InventoryMemoryStore>();
        services.AddSwLicenseWatcherInfrastructure();
        services.AddSingleton<IDeviceCertificateAuthority>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<DeviceEnrollmentOptions>>().Value.CaKeyPath;
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, path);
            }

            return new FileDeviceCertificateAuthority(path, sp.GetRequiredService<ILogger<FileDeviceCertificateAuthority>>());
        });
        services.AddSingleton<DeviceEnrollmentService>();
        services.AddSingleton<WorkerUpdatePinService>();
        services.AddSingleton<WorkerUpdatePackageStore>();
        services.AddHttpClient<GitHubReleaseClient>((sp, client) =>
        {
            var github = sp.GetRequiredService<IOptions<GitHubUpdateOptions>>().Value;
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = github.Timeout > TimeSpan.Zero ? github.Timeout : TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SwLicenseWatcher.Api");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(github.Token))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", github.Token);
            }
        });
        services.AddTransient<GitHubWorkerUpdateImporter>();
        services.AddSingleton<UserMessageEventHub>();
        services.AddHttpClient(WebhookNotificationSender.HttpClientName, (sp, client) =>
        {
            var timeout = sp.GetRequiredService<IOptions<NotificationOptions>>().Value.Webhook.Timeout;
            client.Timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<INotificationSender, WebhookNotificationSender>();
        services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        services.AddSingleton<NotificationPublisher>();
        services.AddHostedService<NotificationDispatchService>();
        services.AddHostedService<StaleHeartbeatMonitor>();
        services.AddHostedService<SchemaReconcileService>();
        services.AddHttpClient(JwtAccessTokenAuthenticator.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<IJwtAccessTokenAuthenticator, JwtAccessTokenAuthenticator>();
        return services;
    }
}
