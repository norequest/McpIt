using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace McpIt.Runtime.Tests;

public class McpScopeGuardTests
{
    // ---------------------------------------------------------------------------
    // HasScope - null / empty requiredScope
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasScope_NullRequiredScope_ReturnsTrue()
    {
        // Null requiredScope means no gate: always pass.
        Assert.True(McpScopeGuard.HasScope(null, null!));
    }

    [Fact]
    public void HasScope_EmptyRequiredScope_ReturnsTrue()
    {
        Assert.True(McpScopeGuard.HasScope(null, string.Empty));
    }

    // ---------------------------------------------------------------------------
    // HasScope - null accessor / context / user
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasScope_NullAccessor_ReturnsFalse()
    {
        Assert.False(McpScopeGuard.HasScope(null, "read"));
    }

    [Fact]
    public void HasScope_NullHttpContext_ReturnsFalse()
    {
        var accessor = new HttpContextAccessor(); // HttpContext is null by default
        Assert.False(McpScopeGuard.HasScope(accessor, "read"));
    }

    // ---------------------------------------------------------------------------
    // HasScope - scope claim, space-delimited OAuth convention
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasScope_ScopeClaimSingleValue_MatchesExact()
    {
        var accessor = MakeAccessor(("scope", "read"));
        Assert.True(McpScopeGuard.HasScope(accessor, "read"));
    }

    [Fact]
    public void HasScope_ScopeClaimSingleValue_NoMatchDifferentValue()
    {
        var accessor = MakeAccessor(("scope", "write"));
        Assert.False(McpScopeGuard.HasScope(accessor, "read"));
    }

    [Fact]
    public void HasScope_ScopeClaimSpaceDelimited_MatchesFirstToken()
    {
        var accessor = MakeAccessor(("scope", "read write admin"));
        Assert.True(McpScopeGuard.HasScope(accessor, "read"));
    }

    [Fact]
    public void HasScope_ScopeClaimSpaceDelimited_MatchesMiddleToken()
    {
        var accessor = MakeAccessor(("scope", "read write admin"));
        Assert.True(McpScopeGuard.HasScope(accessor, "write"));
    }

    [Fact]
    public void HasScope_ScopeClaimSpaceDelimited_MatchesLastToken()
    {
        var accessor = MakeAccessor(("scope", "read write admin"));
        Assert.True(McpScopeGuard.HasScope(accessor, "admin"));
    }

    [Fact]
    public void HasScope_ScopeClaimSpaceDelimited_NoMatchAbsentScope()
    {
        var accessor = MakeAccessor(("scope", "read write"));
        Assert.False(McpScopeGuard.HasScope(accessor, "admin"));
    }

    [Fact]
    public void HasScope_ScopeClaimSpaceDelimited_NoPartialMatch()
    {
        // "readwrite" should NOT match "read" -- token equality only.
        var accessor = MakeAccessor(("scope", "readwrite"));
        Assert.False(McpScopeGuard.HasScope(accessor, "read"));
    }

    // ---------------------------------------------------------------------------
    // HasScope - scp claim (Azure AD / MSAL convention)
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasScope_ScpClaimSingleValue_Matches()
    {
        var accessor = MakeAccessor(("scp", "orders.read"));
        Assert.True(McpScopeGuard.HasScope(accessor, "orders.read"));
    }

    [Fact]
    public void HasScope_ScpClaimSpaceDelimited_Matches()
    {
        var accessor = MakeAccessor(("scp", "orders.read orders.write"));
        Assert.True(McpScopeGuard.HasScope(accessor, "orders.write"));
    }

    [Fact]
    public void HasScope_ScpClaimMissing_RequiredScopeSet_ReturnsFalse()
    {
        // User has no scope/scp claims at all.
        var accessor = MakeAccessor(("sub", "user123"));
        Assert.False(McpScopeGuard.HasScope(accessor, "read"));
    }

    // ---------------------------------------------------------------------------
    // HasScope - multiple scope claims (each individual)
    // ---------------------------------------------------------------------------

    [Fact]
    public void HasScope_MultipleIndividualScopeClaims_MatchesOne()
    {
        // Some IdPs issue multiple individual scope claims rather than one space-delimited one.
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("scope", "read"),
            new Claim("scope", "write"),
        });
        ctx.User = new ClaimsPrincipal(identity);
        var accessor = new HttpContextAccessor { HttpContext = ctx };

        Assert.True(McpScopeGuard.HasScope(accessor, "write"));
    }

    // ---------------------------------------------------------------------------
    // Denied - JSON structure
    // ---------------------------------------------------------------------------

    [Fact]
    public void Denied_ReturnsValidJson()
    {
        var json = McpScopeGuard.Denied("myTool", "admin");
        // Must be parseable JSON with no exception.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
    }

    [Fact]
    public void Denied_HasErrorForbidden()
    {
        var json = McpScopeGuard.Denied("myTool", "admin");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("forbidden", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Denied_HasToolName()
    {
        var json = McpScopeGuard.Denied("getOrder", "read");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("getOrder", doc.RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public void Denied_HasRequiredScope()
    {
        var json = McpScopeGuard.Denied("getOrder", "orders:read");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("orders:read", doc.RootElement.GetProperty("requiredScope").GetString());
    }

    [Fact]
    public void Denied_EscapesSpecialCharactersInJson()
    {
        // Tool name with a double-quote character must produce valid JSON.
        var json = McpScopeGuard.Denied("tool\"name", "scope\"value");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("tool\"name", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("scope\"value", doc.RootElement.GetProperty("requiredScope").GetString());
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IHttpContextAccessor MakeAccessor(params (string Type, string Value)[] claims)
    {
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)));
        ctx.User = new ClaimsPrincipal(identity);
        return new HttpContextAccessor { HttpContext = ctx };
    }
}
