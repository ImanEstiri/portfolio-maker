namespace CvMaker.Shared;

/// <summary>
/// The development stand-in for a bearer token, shared so the API and the web client cannot
/// drift apart on its name.
/// </summary>
public static class DevAuth
{
    /// <summary>
    /// Names the caller when there is no token. An API accepts it as an identity
    /// (<c>UserContext</c>) only when it is running in Development <i>and</i> has no token
    /// validation configured; everywhere else, an endpoint that needs a user needs a real
    /// token.
    /// </summary>
    public const string UserHeader = "X-CvMaker-Dev-User";
}
