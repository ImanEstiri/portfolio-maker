namespace CvMaker.Web.Services;

/// <summary>
/// Local development without an identity service: the client acts as one fixed user, and
/// says so, instead of carrying a token.
///
/// <c>docker compose up</c> runs the API in Development with no token validation configured,
/// where it takes the caller from the <see cref="CvMaker.Shared.DevAuth.UserHeader"/> header.
/// Nothing in the browser could send that header, so the quick start used to produce a UI
/// that could not get past its own sign-in wall: every page asked for an account on an
/// identity service the compose file does not run.
///
/// Off unless <c>DevUser</c> is set in <c>appsettings.json</c> — the <c>DEV_USER</c>
/// variable, via <c>docker-entrypoint.sh</c>. It grants nothing on its own: only an API
/// running in Development with no token validation configured accepts the header, so pointed
/// at a deployed API — or at a Development one that has <c>Auth__Authority</c> set — this
/// mode gets a 401 from every endpoint that needs a user, not access to one.
/// </summary>
public sealed class DevUser(string? name)
{
    public string? Name { get; } = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    public bool IsEnabled => Name is not null;
}
