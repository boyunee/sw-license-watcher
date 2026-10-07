using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SwLicenseWatcher.Api;
using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api.Tests;

public class JwtAccessTokenAuthenticatorTests
{
    private const string Issuer = "https://issuer.example";
    private const string Audience = "api://sw-license-watcher";

    [Fact]
    public async Task Disabled_jwt_never_authenticates()
    {
        var authenticator = CreateAuthenticator(new ApiJwtOptions(), CreateKey());

        var result = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(CreateKey()),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(authenticator.IsEnabled);
    }

    [Fact]
    public async Task Valid_token_is_accepted_as_admin()
    {
        var key = CreateKey();
        var jwt = EnabledJwt();
        jwt.RequiredScope = "admin";
        var authenticator = CreateAuthenticator(jwt, key);
        var token = CreateToken(key, claims: new Dictionary<string, object> { ["scp"] = "admin" });

        var result = await authenticator.AuthenticateAdminAsync("Bearer " + token, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
    }

    [Fact]
    public async Task Valid_token_is_rejected_when_scope_and_role_are_not_required()
    {
        var key = CreateKey();
        var authenticator = CreateAuthenticator(EnabledJwt(), key);

        var result = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(key),
            CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var key = CreateKey();
        var jwt = EnabledJwt();
        jwt.RequiredScope = "admin";
        var authenticator = CreateAuthenticator(jwt, key);
        var token = CreateToken(
            key,
            expires: DateTime.UtcNow.AddMinutes(-10),
            notBefore: DateTime.UtcNow.AddMinutes(-20),
            claims: new Dictionary<string, object> { ["scp"] = "admin" });

        var result = await authenticator.AuthenticateAdminAsync("Bearer " + token, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var key = CreateKey();
        var jwt = EnabledJwt();
        jwt.RequiredScope = "admin";
        var authenticator = CreateAuthenticator(jwt, key);
        var token = CreateToken(
            key,
            audience: "api://other",
            claims: new Dictionary<string, object> { ["scp"] = "admin" });

        var result = await authenticator.AuthenticateAdminAsync("Bearer " + token, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Missing_required_scope_is_rejected()
    {
        var key = CreateKey();
        var jwt = EnabledJwt();
        jwt.RequiredScope = "admin";
        var authenticator = CreateAuthenticator(jwt, key);

        var withoutScope = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(key),
            CancellationToken.None);
        var withScope = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(key, claims: new Dictionary<string, object> { ["scp"] = "admin extra" }),
            CancellationToken.None);

        Assert.False(withoutScope.Succeeded);
        Assert.True(withScope.Succeeded);
    }

    [Fact]
    public async Task Missing_required_role_is_rejected()
    {
        var key = CreateKey();
        var jwt = EnabledJwt();
        jwt.RequiredRole = "inventory.admin";
        var authenticator = CreateAuthenticator(jwt, key);

        var withoutRole = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(key),
            CancellationToken.None);
        var withRole = await authenticator.AuthenticateAdminAsync(
            "Bearer " + CreateToken(key, claims: new Dictionary<string, object> { ["roles"] = "inventory.admin" }),
            CancellationToken.None);

        Assert.False(withoutRole.Succeeded);
        Assert.True(withRole.Succeeded);
    }

    [Fact]
    public async Task Static_looking_token_is_not_treated_as_jwt()
    {
        var key = CreateKey();
        var authenticator = CreateAuthenticator(EnabledJwt(), key);

        var result = await authenticator.AuthenticateAdminAsync(
            "Bearer agent-token-abcdefghijklmnopqrst",
            CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("bearer aaa.bbb.ccc")]
    public void TryReadBearerToken_rejects_non_jwt_headers(string? header)
    {
        Assert.False(JwtAccessTokenAuthenticator.TryReadBearerToken(header, out _));
    }

    private static JwtAccessTokenAuthenticator CreateAuthenticator(ApiJwtOptions jwt, RsaSecurityKey key)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = "name",
            RoleClaimType = "roles"
        };

        return new JwtAccessTokenAuthenticator(
            jwt,
            NullLogger<JwtAccessTokenAuthenticator>.Instance,
            parameters);
    }

    private static ApiJwtOptions EnabledJwt() =>
        new()
        {
            Authority = Issuer,
            Audience = Audience
        };

    private static RsaSecurityKey CreateKey()
    {
        using var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = "test-key" };
    }

    private static string CreateToken(
        RsaSecurityKey key,
        string audience = Audience,
        DateTime? expires = null,
        DateTime? notBefore = null,
        IDictionary<string, object>? claims = null)
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            Claims = claims
        });
    }
}
