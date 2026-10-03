using System.Net;
using System.Security.Claims;
using CvMaker.Api;
using CvMaker.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CvMaker.Tests;

/// <summary>
/// The development-identity shortcut: an unauthenticated caller is treated as a named user,
/// from the <c>X-CvMaker-Dev-User</c> header. It is a convenience for running the stack
/// without an identity service, and the whole risk is that it is also a way to be anyone —
/// so what these pin is where it does <b>not</b> apply.
///
/// The rule under test is that it applies in Development <i>with no token validation
/// configured</i>, and nowhere else. The environment name alone used to decide it, which made
/// a Development API pointed at a real identity service serve everything anonymously.
/// </summary>
public class DevIdentityTests
{
    private const string Development = "Development";
    private const string Production = "Production";

    private static readonly Dictionary<string, string?> NoValidation = new();
    private static readonly Dictionary<string, string?> Jwks = new() { ["Auth:Authority"] = "https://auth.example" };
    private static readonly Dictionary<string, string?> SharedSecret = new() { ["Jwt:SecretKey"] = "not-a-real-secret" };

    // ── The predicate ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Development, null, null, true)]
    [InlineData(Development, "https://auth.example", null, false)]
    [InlineData(Development, null, "a-secret", false)]
    [InlineData(Development, "https://auth.example", "a-secret", false)]
    // Blank is unset: an empty environment variable must not switch a mode on by existing.
    [InlineData(Development, "", "   ", true)]
    [InlineData(Production, null, null, false)]
    [InlineData(Production, "https://auth.example", null, false)]
    [InlineData("Staging", null, null, false)]
    public void AllowsDevIdentity_needs_Development_and_no_validation(
        string environment, string? authority, string? secret, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = authority,
            ["Jwt:SecretKey"] = secret
        }).Build();

        Assert.Equal(expected, JwtExtensions.AllowsDevIdentity(config, new FakeEnvironment(environment)));
    }

    // ── UserId ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Development_without_validation_takes_the_user_from_the_header()
    {
        var http = Context(Development, NoValidation, devHeader: "alice");
        Assert.Equal("alice", http.UserId());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Development_without_validation_falls_back_to_dev_user(string? header)
    {
        var http = Context(Development, NoValidation, devHeader: header);
        Assert.Equal("dev-user", http.UserId());
    }

    [Fact]
    public void Development_with_jwks_configured_ignores_the_header()
    {
        var http = Context(Development, Jwks, devHeader: "alice");
        Assert.Throws<InvalidOperationException>(() => http.UserId());
    }

    [Fact]
    public void Development_with_a_shared_secret_configured_ignores_the_header()
    {
        var http = Context(Development, SharedSecret, devHeader: "alice");
        Assert.Throws<InvalidOperationException>(() => http.UserId());
    }

    [Fact]
    public void Production_ignores_the_header()
    {
        var http = Context(Production, NoValidation, devHeader: "alice");
        Assert.Throws<InvalidOperationException>(() => http.UserId());
    }

    [Theory]
    [InlineData(Development, false)]
    [InlineData(Development, true)]
    [InlineData(Production, false)]
    public void A_validated_subject_always_wins_over_the_header(string environment, bool validationConfigured)
    {
        var http = Context(environment, validationConfigured ? Jwks : NoValidation,
            devHeader: "someone-else", subject: "real-user");

        Assert.Equal("real-user", http.UserId());
    }

    [Fact]
    public void The_name_identifier_claim_counts_as_the_subject_too()
    {
        var http = Context(Production, Jwks, nameIdentifier: "mapped-user");
        Assert.Equal("mapped-user", http.UserId());
    }

    // ── Rate-limit partitioning ─────────────────────────────────────────────────

    [Fact]
    public void Rate_limiting_keys_on_the_header_only_where_the_dev_identity_applies()
    {
        var http = Context(Development, NoValidation, devHeader: "alice", remoteIp: "10.1.2.3");
        Assert.Equal("alice", http.RateLimitPartitionKey());
    }

    [Theory]
    [InlineData(Production)]
    [InlineData(Development)]
    public void Rate_limiting_does_not_trust_a_header_it_would_not_honour_as_an_identity(string environment)
    {
        // Otherwise an unauthenticated client picks its own partition: a new header value per
        // request is a new bucket per request, and the limiter limits nothing.
        var config = environment == Development ? Jwks : NoValidation;
        var http = Context(environment, config, devHeader: "fresh-every-time", remoteIp: "10.1.2.3");

        Assert.Equal("10.1.2.3", http.RateLimitPartitionKey());
    }

    [Fact]
    public void Rate_limiting_falls_back_to_anonymous_with_no_address()
    {
        var http = Context(Production, NoValidation);
        Assert.Equal("anonymous", http.RateLimitPartitionKey());
    }

    [Fact]
    public void Rate_limiting_prefers_the_subject()
    {
        var http = Context(Production, Jwks, devHeader: "alice", subject: "real-user", remoteIp: "10.1.2.3");
        Assert.Equal("real-user", http.RateLimitPartitionKey());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static DefaultHttpContext Context(
        string environment,
        Dictionary<string, string?> config,
        string? devHeader = null,
        string? subject = null,
        string? nameIdentifier = null,
        string? remoteIp = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build())
            .AddSingleton<IHostEnvironment>(new FakeEnvironment(environment))
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };

        if (devHeader is not null)
            http.Request.Headers[UserContext.DevUserHeader] = devHeader;

        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim("sub", subject));
        if (nameIdentifier is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        if (claims.Count > 0)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));

        if (remoteIp is not null)
            http.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        return http;
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "CvMaker.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
