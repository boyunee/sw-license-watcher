using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api;

internal interface IJwtAccessTokenAuthenticator
{
    bool IsEnabled { get; }

    Task<JwtAdminAuthenticationResult> AuthenticateAdminAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken);
}

internal sealed class JwtAdminAuthenticationResult
{
    internal static JwtAdminAuthenticationResult Failed { get; } = new(false, null);

    internal JwtAdminAuthenticationResult(bool succeeded, ClaimsPrincipal? principal)
    {
        Succeeded = succeeded;
        Principal = principal;
    }

    internal bool Succeeded { get; }

    internal ClaimsPrincipal? Principal { get; }
}

internal sealed class JwtAccessTokenAuthenticator : IJwtAccessTokenAuthenticator
{
    internal const string HttpClientName = "JwtOidcMetadata";

    private readonly ApiJwtOptions _jwt;
    private readonly ILogger<JwtAccessTokenAuthenticator> _logger;
    private readonly TokenValidationParameters? _fixedParameters;
    private readonly IConfigurationManager<OpenIdConnectConfiguration>? _configuration;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public JwtAccessTokenAuthenticator(
        IOptions<ApiSecurityOptions> security,
        IHttpClientFactory httpClientFactory,
        ILogger<JwtAccessTokenAuthenticator> logger)
        : this(security.Value.Jwt, logger, CreateConfigurationManager(security.Value.Jwt, httpClientFactory), null)
    {
    }

    internal JwtAccessTokenAuthenticator(
        ApiJwtOptions jwt,
        ILogger<JwtAccessTokenAuthenticator> logger,
        TokenValidationParameters parameters)
        : this(jwt, logger, null, parameters)
    {
    }

    private JwtAccessTokenAuthenticator(
        ApiJwtOptions jwt,
        ILogger<JwtAccessTokenAuthenticator> logger,
        IConfigurationManager<OpenIdConnectConfiguration>? configuration,
        TokenValidationParameters? fixedParameters)
    {
        _jwt = jwt;
        _logger = logger;
        _configuration = configuration;
        _fixedParameters = fixedParameters;
    }

    public bool IsEnabled => _jwt.IsEnabled;

    public async Task<JwtAdminAuthenticationResult> AuthenticateAdminAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return JwtAdminAuthenticationResult.Failed;
        }

        if (string.IsNullOrWhiteSpace(_jwt.RequiredScope) && string.IsNullOrWhiteSpace(_jwt.RequiredRole))
        {
            return JwtAdminAuthenticationResult.Failed;
        }

        if (!TryReadBearerToken(authorizationHeader, out var token))
        {
            return JwtAdminAuthenticationResult.Failed;
        }

        try
        {
            var parameters = await CreateValidationParametersAsync(cancellationToken);
            var result = await _handler.ValidateTokenAsync(token, parameters);
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            {
                return JwtAdminAuthenticationResult.Failed;
            }

            if (!HasRequiredScope(jwt) || !HasRequiredRole(jwt))
            {
                return JwtAdminAuthenticationResult.Failed;
            }

            var identity = result.ClaimsIdentity ?? new ClaimsIdentity("jwt");
            return new JwtAdminAuthenticationResult(true, new ClaimsPrincipal(identity));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "JWT access token validation failed.");
            return JwtAdminAuthenticationResult.Failed;
        }
    }

    internal static bool TryReadBearerToken(string? authorizationHeader, out string token)
    {
        const string prefix = "Bearer ";
        var header = authorizationHeader ?? string.Empty;
        if (!header.StartsWith(prefix, StringComparison.Ordinal) || header.Length <= prefix.Length)
        {
            token = string.Empty;
            return false;
        }

        token = header[prefix.Length..];
        var firstDot = token.IndexOf('.');
        var lastDot = token.LastIndexOf('.');
        return firstDot > 0 && lastDot > firstDot && lastDot < token.Length - 1;
    }

    private async Task<TokenValidationParameters> CreateValidationParametersAsync(CancellationToken cancellationToken)
    {
        if (_fixedParameters is not null)
        {
            return _fixedParameters;
        }

        if (_configuration is null)
        {
            throw new InvalidOperationException("JWT configuration manager is not available.");
        }

        var config = await _configuration.GetConfigurationAsync(cancellationToken);
        var issuers = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(config.Issuer))
        {
            issuers.Add(config.Issuer.TrimEnd('/'));
        }

        issuers.Add(_jwt.Authority.TrimEnd('/'));

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ClockSkew = _jwt.ClockSkew,
            NameClaimType = string.IsNullOrWhiteSpace(_jwt.NameClaimType) ? "name" : _jwt.NameClaimType,
            RoleClaimType = string.IsNullOrWhiteSpace(_jwt.RoleClaimType) ? "roles" : _jwt.RoleClaimType
        };
    }

    private bool HasRequiredScope(JsonWebToken jwt)
    {
        if (string.IsNullOrWhiteSpace(_jwt.RequiredScope))
        {
            return true;
        }

        return ReadValues(jwt, "scp")
            .Concat(ReadValues(jwt, "scope"))
            .SelectMany(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(_jwt.RequiredScope, StringComparer.Ordinal);
    }

    private bool HasRequiredRole(JsonWebToken jwt)
    {
        if (string.IsNullOrWhiteSpace(_jwt.RequiredRole))
        {
            return true;
        }

        var claimType = string.IsNullOrWhiteSpace(_jwt.RoleClaimType) ? "roles" : _jwt.RoleClaimType;
        return ReadValues(jwt, claimType)
            .Concat(ReadValues(jwt, "roles"))
            .SelectMany(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(_jwt.RequiredRole, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ReadValues(JsonWebToken jwt, string claimType)
    {
        foreach (var claim in jwt.Claims)
        {
            if (string.Equals(claim.Type, claimType, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(claim.Value))
            {
                yield return claim.Value;
            }
        }
    }

    private static IConfigurationManager<OpenIdConnectConfiguration>? CreateConfigurationManager(
        ApiJwtOptions jwt,
        IHttpClientFactory httpClientFactory)
    {
        if (!jwt.IsEnabled)
        {
            return null;
        }

        var metadataAddress = jwt.ResolveMetadataAddress();
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var retriever = new HttpDocumentRetriever(httpClient)
        {
            RequireHttps = metadataAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        };
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            retriever);
    }
}
