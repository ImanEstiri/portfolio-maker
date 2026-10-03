using System.Security.Claims;
using CvMaker.Api.Auth;
using CvMaker.Shared;

namespace CvMaker.Api;

public static class UserContext
{
    /// <summary>
    /// Stands in for a bearer token in Development. CvMaker.Web sends it when it is
    /// configured with a <c>DevUser</c>, which is how <c>docker compose up</c> gets a usable
    /// UI without running an identity service.
    /// </summary>
    public const string DevUserHeader = DevAuth.UserHeader;

    /// <summary>
    /// The owning user for the current request.
    ///
    /// The real source is the JWT <c>sub</c> claim, validated against the key set
    /// authservice publishes (<c>Auth/JwtExtensions.cs</c>). Without one, a header can stand
    /// in — which keeps every endpoint written against a real per-user key from the start,
    /// rather than being retrofitted with ownership checks later.
    ///
    /// <para>
    /// The fallback exists only where <see cref="JwtExtensions.AllowsDevIdentity"/> says so:
    /// Development with no token validation configured. Everywhere else the user endpoints sit
    /// behind <c>RequireAuthorization</c>, so an unauthenticated request never reaches here,
    /// and if one somehow does it throws rather than become an anonymous shared account.
    /// </para>
    /// </summary>
    public static string UserId(this HttpContext http)
    {
        var sub = Subject(http);
        if (!string.IsNullOrEmpty(sub)) return sub;

        if (DevIdentityAllowed(http))
            return DevUserFromHeader(http) ?? "dev-user";

        throw new InvalidOperationException(
            "No authenticated user on the request. This endpoint must be behind authorization.");
    }

    /// <summary>
    /// A partition key for rate limiting that never throws.
    ///
    /// <see cref="UserId"/> deliberately throws on an unauthenticated request
    /// where there is no dev identity, which is right for an endpoint handler and wrong
    /// inside a rate-limiter partitioner — that runs before the handler, and an
    /// exception there becomes a 500 instead of a 401.
    ///
    /// The dev header is trusted here on the same terms as in <see cref="UserId"/>. Anywhere
    /// else it is just a string the caller chose, and keying on it would let an
    /// unauthenticated client mint a fresh rate-limit partition per request.
    /// </summary>
    public static string RateLimitPartitionKey(this HttpContext http)
    {
        var sub = Subject(http);
        if (!string.IsNullOrEmpty(sub)) return sub;

        if (DevIdentityAllowed(http) && DevUserFromHeader(http) is { } devUser)
            return devUser;

        return http.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    private static string? Subject(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub");

    private static string? DevUserFromHeader(HttpContext http) =>
        http.Request.Headers.TryGetValue(DevUserHeader, out var header) &&
        !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : null;

    private static bool DevIdentityAllowed(HttpContext http) =>
        JwtExtensions.AllowsDevIdentity(
            http.RequestServices.GetRequiredService<IConfiguration>(),
            http.RequestServices.GetRequiredService<IHostEnvironment>());
}
