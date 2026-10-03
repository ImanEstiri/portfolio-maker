using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace CvMaker.Api.Auth;

public static class JwtExtensions
{
    /// <summary>
    /// Validates tokens minted by the cv-maker instance of
    /// <see href="https://github.com/konradcinkusz/authservice">authservice</see>.
    ///
    /// Three modes, in the order they are looked for:
    ///
    /// <list type="number">
    /// <item><b>JWKS</b> (<c>Auth:Authority</c>). The deployed shape. authservice holds the
    /// private key and this process fetches the public half from its
    /// <c>/.well-known/jwks.json</c>. This API can verify a token and cannot mint one — which
    /// is the entire point, and what the architecture standard's "exactly one service holds a
    /// signing key" rule requires.</item>
    ///
    /// <item><b>Shared secret</b> (<c>Jwt:SecretKey</c>). Legacy HS256, kept so a deployment
    /// mid-migration still works. Under it the validation key <i>is</i> the signing key: this
    /// process can mint tokens for any user with any role. It is accepted, and it is warned
    /// about, because a CV renderer holding issuance rights over the identity system is a
    /// posture nobody should arrive at silently.</item>
    ///
    /// <item><b>Neither</b>, in Development only. <c>UserContext</c>'s
    /// <c>X-CvMaker-Dev-User</c> header stands in for a token — see
    /// <see cref="AllowsDevIdentity"/>, which is the one place that decides it.</item>
    /// </list>
    ///
    /// Issuer and audience are cv-maker's own, not authservice's <c>AuthService</c> default:
    /// two products sharing the defaults would each accept the other's tokens.
    /// </summary>
    public static IServiceCollection AddCvMakerJwtAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var authority = configuration["Auth:Authority"];
        var secret = configuration["Jwt:SecretKey"];
        var issuer = configuration["Jwt:Issuer"] ?? "CvMaker";
        var audience = configuration["Jwt:Audience"] ?? "CvMaker";

        if (!IsTokenValidationConfigured(configuration))
        {
            if (!environment.IsDevelopment())
            {
                // Fail to start rather than serve unauthenticated. Missing auth configuration
                // in production is a misconfiguration, and the worst possible response is to
                // keep running.
                throw new InvalidOperationException(
                    "No token validation is configured. Set Auth:Authority to the base URL of the " +
                    "authservice instance that issues cv-maker's tokens (preferred), or Jwt:SecretKey " +
                    "to its HS256 signing secret.");
            }

            // Development without auth. The middleware still needs its services registered:
            // UseAuthorization throws outright when AddAuthorization has not been called, and
            // UseAuthentication needs IAuthenticationSchemeProvider from AddAuthentication.
            // Registering both with no scheme makes the middleware a harmless no-op instead of
            // a startup crash — which is what the documented dev-user mode used to be.
            services.AddAuthentication();
            services.AddAuthorization();
            return services;
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };

                if (!string.IsNullOrWhiteSpace(authority))
                {
                    // Point at the discovery document explicitly rather than setting Authority
                    // and letting the handler append the path. authservice's `iss` is a bare
                    // string, not its URL, so the issuer is validated against Jwt:Issuer above
                    // and the metadata is used only to find the keys.
                    options.MetadataAddress =
                        $"{authority.TrimEnd('/')}/.well-known/openid-configuration";

                    // Signing keys arrive from the JWKS; nothing here holds key material.
                    options.RequireHttpsMetadata = !environment.IsDevelopment();
                }
                else
                {
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret!));
                    options.RequireHttpsMetadata = !environment.IsDevelopment();
                }
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// True when either validation mode is configured: <c>Auth:Authority</c> (JWKS) or
    /// <c>Jwt:SecretKey</c> (shared secret). Blank counts as unset, so an empty environment
    /// variable cannot switch a mode on by existing.
    /// </summary>
    public static bool IsTokenValidationConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Auth:Authority"]) ||
        !string.IsNullOrWhiteSpace(configuration["Jwt:SecretKey"]);

    /// <summary>
    /// Whether this process may treat an unauthenticated caller as a development user:
    /// Development <b>and</b> no token validation configured.
    ///
    /// Both halves matter. The environment name alone is not a safe switch, because it is one
    /// variable away from being set wrong on a deployment: with it as the only test, a
    /// Development API pointed at a real identity service would still serve every endpoint
    /// anonymously and let any caller become any user by naming them in a header. Tying the
    /// fallback to the absence of validation means that configuring authentication switches
    /// the shortcut off, whatever the environment says.
    ///
    /// This is the single decision behind three behaviours — the identity fallback in
    /// <c>UserContext</c>, whether the user endpoints require authorization, and whether the
    /// rate limiter trusts the dev header — so they cannot disagree with each other.
    /// </summary>
    public static bool AllowsDevIdentity(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevelopment() && !IsTokenValidationConfigured(configuration);

    /// <summary>
    /// Describes the mode <see cref="AddCvMakerJwtAuth"/> resolved, for the startup log. An
    /// operator should be able to see which posture the process actually came up in rather
    /// than infer it from which settings happen to be present.
    /// </summary>
    public static string DescribeAuthMode(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Auth:Authority"]))
            return $"JWKS ({configuration["Auth:Authority"]})";

        if (!string.IsNullOrWhiteSpace(configuration["Jwt:SecretKey"]))
            return "shared HS256 secret";

        return AllowsDevIdentity(configuration, environment) ? "disabled (dev-user header)" : "none";
    }
}
