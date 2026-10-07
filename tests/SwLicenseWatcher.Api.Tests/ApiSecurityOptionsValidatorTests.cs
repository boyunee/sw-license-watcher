using SwLicenseWatcher.Core;

namespace SwLicenseWatcher.Api.Tests;

public class ApiSecurityOptionsValidatorTests
{
    private const string Usable = "abcdefghijklmnopqrstuvwxyz012345";
    private const string Agent = "agent-token-abcdefghijklmnopqrst";
    private const string Admin = "admin-token-abcdefghijklmnopqrst";

    [Fact]
    public void HasRequiredRoleTokens_accepts_distinct_agent_and_admin_tokens()
    {
        Assert.True(ApiSecurityOptionsValidator.HasRequiredRoleTokens(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = Admin
        }));
    }

    [Fact]
    public void HasRequiredRoleTokens_rejects_legacy_token_only()
    {
        Assert.False(ApiSecurityOptionsValidator.HasRequiredRoleTokens(new ApiSecurityOptions { Token = Usable }));
    }

    [Fact]
    public void HasRequiredRoleTokens_rejects_when_either_role_token_is_missing()
    {
        Assert.False(ApiSecurityOptionsValidator.HasRequiredRoleTokens(new ApiSecurityOptions()));
        Assert.False(ApiSecurityOptionsValidator.HasRequiredRoleTokens(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = string.Empty
        }));
        Assert.False(ApiSecurityOptionsValidator.HasRequiredRoleTokens(new ApiSecurityOptions
        {
            Token = " ",
            AgentToken = string.Empty,
            AdminToken = "short"
        }));
    }

    [Fact]
    public void HasValidConfiguredTokenLengths_rejects_a_short_configured_token()
    {
        Assert.False(ApiSecurityOptionsValidator.HasValidConfiguredTokenLengths(new ApiSecurityOptions
        {
            AgentToken = "too-short",
            AdminToken = Admin
        }));
        Assert.True(ApiSecurityOptionsValidator.HasValidConfiguredTokenLengths(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = Admin
        }));
    }

    [Fact]
    public void RejectsLegacySharedToken_requires_token_to_be_empty()
    {
        Assert.True(ApiSecurityOptionsValidator.RejectsLegacySharedToken(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = Admin
        }));
        Assert.False(ApiSecurityOptionsValidator.RejectsLegacySharedToken(new ApiSecurityOptions
        {
            Token = Usable,
            AgentToken = Agent,
            AdminToken = Admin
        }));
    }

    [Fact]
    public void HasDistinctRoleTokens_rejects_matching_agent_and_admin_tokens()
    {
        Assert.False(ApiSecurityOptionsValidator.HasDistinctRoleTokens(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = Agent
        }));
    }

    [Fact]
    public void HasDistinctRoleTokens_accepts_distinct_role_tokens()
    {
        Assert.True(ApiSecurityOptionsValidator.HasDistinctRoleTokens(new ApiSecurityOptions
        {
            AgentToken = Agent,
            AdminToken = Admin
        }));
    }

    [Fact]
    public void HasValidJwt_allows_empty_authority()
    {
        Assert.True(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions()));
    }

    [Fact]
    public void HasValidJwt_requires_audience_and_http_authority()
    {
        Assert.False(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt = { Authority = "https://login.example/tenant/v2.0" }
        }));
        Assert.False(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt =
            {
                Authority = "not-a-uri",
                Audience = "api://swlw"
            }
        }));
        Assert.False(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt =
            {
                Authority = "https://login.example/tenant/v2.0",
                Audience = "api://swlw",
                ClockSkew = TimeSpan.FromSeconds(-1)
            }
        }));
        Assert.False(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt =
            {
                Authority = "https://login.example/tenant/v2.0",
                Audience = "api://swlw"
            }
        }));
        Assert.False(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt =
            {
                Authority = "http://login.example/tenant/v2.0",
                Audience = "api://swlw",
                RequiredScope = "admin"
            }
        }));
        Assert.True(ApiSecurityOptionsValidator.HasValidJwt(new ApiSecurityOptions
        {
            Jwt =
            {
                Authority = "https://login.example/tenant/v2.0",
                Audience = "api://swlw",
                RequiredRole = "inventory.admin"
            }
        }));
    }

    [Fact]
    public void ResolveMetadataAddress_defaults_to_well_known_openid_configuration()
    {
        var jwt = new ApiJwtOptions
        {
            Authority = "https://login.example/tenant/v2.0/"
        };

        Assert.Equal(
            "https://login.example/tenant/v2.0/.well-known/openid-configuration",
            jwt.ResolveMetadataAddress());

        jwt.MetadataAddress = "https://login.example/custom/openid-configuration/";
        Assert.Equal(
            "https://login.example/custom/openid-configuration",
            jwt.ResolveMetadataAddress());
    }
}
