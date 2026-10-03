using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CvMaker.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CvMaker.Web.Services;

public sealed record LoginRequest(string Email, string Password);

public sealed record RegisterRequest(
    string Email,
    string Password,
    string AcceptedTermsVersion,
    string AcceptedPrivacyVersion,
    string? Locale = null);

public sealed record RefreshRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record TwoFactorLoginRequest(string ChallengeToken, string? Code, string? RecoveryCode);

/// <summary>
/// authservice's token response.
///
/// <c>ExpiresIn</c> is <b>seconds</b>, not an absolute time. This type previously declared a
/// <c>DateTime ExpiresAt</c>, which is not a field authservice sends: it deserialized to
/// <c>DateTime.MinValue</c> on every response, so <c>IsAuthenticated</c> was false immediately
/// after a successful sign-in and the app behaved as though login had silently failed.
/// </summary>
public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType = "Bearer");

/// <summary>Returned by login when the account has a second factor configured.</summary>
public sealed record TwoFactorRequired(bool RequiresTwoFactor, string ChallengeToken, int ExpiresIn);

/// <summary>Returned by registration when the deployment sends verification email.</summary>
public sealed record RegistrationPending(string UserId, string Email, string Message, bool EmailVerificationRequired);

/// <summary>The outcome of a sign-in or registration attempt, as the UI needs to branch on it.</summary>
public enum AuthOutcome
{
    Succeeded,
    Failed,
    TwoFactorRequired,
    EmailVerificationRequired
}

public sealed record AuthResult(AuthOutcome Outcome, string? Error = null, string? ChallengeToken = null)
{
    public static readonly AuthResult Success = new(AuthOutcome.Succeeded);
    public static AuthResult Fail(string message) => new(AuthOutcome.Failed, message);
}

/// <summary>
/// Holds the access token <b>in memory only</b>, and the refresh token in
/// <c>sessionStorage</c>.
///
/// The access token is never persisted: in a WebAssembly app anything the application can
/// read, injected script can read too, and a CV service holds exactly the kind of personal
/// data worth stealing. Keeping the short-lived credential out of storage keeps the window in
/// which a successful injection is useful down to one token lifetime.
///
/// The refresh token has to survive a reload or there is no session at all — Blazor reboots
/// the entire runtime on navigation-to-self, so F5 mid-generation signed the user out. The
/// intended home for it is an httpOnly cookie the JavaScript context cannot touch, and
/// authservice does not set one: it returns both tokens in a JSON body.
///
/// So this is a deliberate interim, written down rather than implied:
///
/// <list type="bullet">
/// <item><c>sessionStorage</c>, not <c>localStorage</c> — scoped to the tab and cleared when
/// it closes, so the token does not outlive the visit or leak across tabs.</item>
/// <item>Cleared on sign-out, and on any refresh failure, so a revoked token is not kept
/// around being retried.</item>
/// <item>It is readable by injected script. That is the accepted cost, and the reason the
/// tokens are single-use and rotated on every refresh: a stolen one is detectable, because
/// authservice revokes the whole rotation family when a used token is presented again.</item>
/// </list>
///
/// The httpOnly-cookie version needs a change in authservice, not here.
/// </summary>
public sealed class TokenStore(IJSRuntime js, DevUser devUser)
{
    private const string RefreshTokenKey = "cvmaker.refresh";

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public event Action? Changed;

    // A configured dev user counts as signed in, so every guarded page renders; the API, not
    // this flag, decides whether that identity is accepted (see DevUser).
    public bool IsAuthenticated =>
        devUser.IsEnabled || (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt);

    /// <summary>True once the startup restore attempt has finished, successfully or not.</summary>
    public bool IsRestored { get; private set; }

    public string? AccessToken => _accessToken;

    public string? RefreshToken => _refreshToken;

    public AuthenticationHeaderValue? Header =>
        _accessToken is null ? null : new AuthenticationHeaderValue("Bearer", _accessToken);

    public async Task SetAsync(TokenResponse tokens)
    {
        _accessToken = tokens.AccessToken;
        _refreshToken = tokens.RefreshToken;

        // A minute of slack so a request started just before expiry does not arrive just
        // after it — and so the refresh handler gets a chance before the server says no.
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, tokens.ExpiresIn)).AddMinutes(-1);

        await WriteRefreshTokenAsync(tokens.RefreshToken);
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        _expiresAt = DateTimeOffset.MinValue;

        await WriteRefreshTokenAsync(null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Reads the refresh token left behind by a previous page load. Called once at startup;
    /// the caller exchanges it for a fresh pair.
    /// </summary>
    public async Task<string?> ReadStoredRefreshTokenAsync()
    {
        try
        {
            return await js.InvokeAsync<string?>("sessionStorage.getItem", RefreshTokenKey);
        }
        catch (JSException)
        {
            // sessionStorage is unavailable in some privacy modes. Losing the session on
            // reload is a worse experience, not a broken app.
            return null;
        }
    }

    public void MarkRestored()
    {
        IsRestored = true;
        Changed?.Invoke();
    }

    private async Task WriteRefreshTokenAsync(string? token)
    {
        try
        {
            if (token is null)
                await js.InvokeVoidAsync("sessionStorage.removeItem", RefreshTokenKey);
            else
                await js.InvokeVoidAsync("sessionStorage.setItem", RefreshTokenKey, token);
        }
        catch (JSException)
        {
            // See above — storage being unavailable must not break signing in.
        }
    }
}

/// <summary>
/// Talks to the cv-maker authservice instance.
///
/// Deliberately thin: authservice owns accounts, consent and credentials, and this client
/// exists to obtain a token and to carry the account screens this app renders. Every method
/// returns a message rather than throwing, because every caller is a form that needs one.
/// </summary>
public sealed class AuthClient(HttpClient http, TokenStore tokens, ConsentVersions consent, DevUser devUser)
{
    public async Task<AuthResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password), ct);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new AuthResult(AuthOutcome.EmailVerificationRequired,
                    "Check your inbox and confirm your email address before signing in.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Never distinguish "no such account" from "wrong password": the difference is
                // an account-enumeration oracle. Lockout is the one exception, and authservice
                // only discloses it to a caller who already proved they know the password.
                var lockout = await ReadLockoutMessageAsync(response, ct);
                return AuthResult.Fail(lockout ?? "Sign-in failed. Check your email and password.");
            }

            return await ReadTokensAsync(response, ct);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("The sign-in service is unreachable.");
        }
    }

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            // authservice rejects a registration that does not accept the *current* versions,
            // so these are fetched from it rather than hardcoded here — otherwise bumping a
            // policy version silently breaks sign-up in this app.
            var versions = await consent.GetAsync(ct);

            var response = await http.PostAsJsonAsync("/api/v1/auth/register",
                new RegisterRequest(email, password, versions.Terms, versions.Privacy,
                    Locale: CultureInfoName()), ct);

            if (!response.IsSuccessStatusCode)
                return AuthResult.Fail(await ReadRegistrationErrorAsync(response, ct));

            // 202: the account exists but cannot sign in until the address is confirmed.
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                var pending = await response.Content.ReadFromJsonAsync<RegistrationPending>(ct);
                return new AuthResult(AuthOutcome.EmailVerificationRequired,
                    pending?.Message ?? "Check your inbox to confirm your email address.");
            }

            return await ReadTokensAsync(response, ct);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("The sign-in service is unreachable.");
        }
    }

    /// <summary>
    /// Completes a sign-in that stopped at the second factor. The challenge token is scoped to
    /// this one login and is useless as an access token, which is what lets the UI carry it
    /// between the two screens.
    /// </summary>
    public async Task<AuthResult> CompleteTwoFactorAsync(
        string challengeToken, string? code, string? recoveryCode, CancellationToken ct = default)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/api/v1/auth/2fa/login",
                new TwoFactorLoginRequest(challengeToken, code, recoveryCode), ct);

            if (!response.IsSuccessStatusCode)
            {
                return AuthResult.Fail(
                    "That code was not accepted. Codes expire after about a minute — try the current one.");
            }

            return await ReadTokensAsync(response, ct);
        }
        catch (HttpRequestException)
        {
            return AuthResult.Fail("The sign-in service is unreachable.");
        }
    }

    /// <summary>
    /// Exchanges the stored refresh token for a new pair.
    ///
    /// Returns false for both "no token" and "the server refused it", because the caller does
    /// the same thing either way: treat the user as signed out. A refusal also clears the
    /// stored token — authservice rotates on every refresh and revokes the whole family when a
    /// used token reappears, so retrying a rejected one only makes things worse.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        var refreshToken = tokens.RefreshToken ?? await tokens.ReadStoredRefreshTokenAsync();
        if (string.IsNullOrWhiteSpace(refreshToken)) return false;

        try
        {
            var response = await http.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), ct);

            if (!response.IsSuccessStatusCode)
            {
                await tokens.ClearAsync();
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                await tokens.ClearAsync();
                return false;
            }

            await tokens.SetAsync(payload);
            return true;
        }
        catch (HttpRequestException)
        {
            // Unreachable is not the same as rejected: keep the token so a transient outage
            // does not sign the user out permanently.
            return false;
        }
    }

    /// <summary>
    /// Restores a session on startup. Blazor WASM reboots the runtime on every navigation to
    /// itself, so without this a page refresh, a deep link or a browser restore all land on an
    /// empty store and render the signed-out state to someone who is signed in.
    /// </summary>
    public async Task RestoreSessionAsync(CancellationToken ct = default)
    {
        try
        {
            // A dev user is signed in by configuration, so there is no session to restore and
            // no identity service to ask.
            if (!devUser.IsEnabled)
                await TryRefreshAsync(ct);
        }
        finally
        {
            tokens.MarkRestored();
        }
    }

    public async Task<string?> ForgotPasswordAsync(string email, CancellationToken ct = default)
    {
        try
        {
            // Always the same answer, whatever the server said. authservice is careful not to
            // confirm whether an address exists; echoing a distinguishable error here would
            // hand back the oracle it just refused to give.
            await http.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest(email), ct);
            return null;
        }
        catch (HttpRequestException)
        {
            return "The sign-in service is unreachable.";
        }
    }

    public async Task<string?> ResetPasswordAsync(
        string email, string token, string newPassword, CancellationToken ct = default)
    {
        try
        {
            var response = await http.PostAsJsonAsync("/api/v1/auth/reset-password",
                new ResetPasswordRequest(email, token, newPassword), ct);

            if (response.IsSuccessStatusCode) return null;

            return await ReadRegistrationErrorAsync(response, ct);
        }
        catch (HttpRequestException)
        {
            return "The sign-in service is unreachable.";
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        // Tell authservice first so the refresh token is revoked server-side; a token that is
        // only forgotten locally is still a live credential if it leaked.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
            request.Headers.Authorization = tokens.Header;
            await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            // Signing out locally must work even when the service is unreachable.
        }

        await tokens.ClearAsync();
    }

    private async Task<AuthResult> ReadTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Login answers 200 with either a token pair or a two-factor challenge, so the body
        // decides which. Reading it as a string once avoids consuming the stream twice.
        var body = await response.Content.ReadAsStringAsync(ct);

        var challenge = TryDeserialize<TwoFactorRequired>(body);
        if (challenge is { RequiresTwoFactor: true })
            return new AuthResult(AuthOutcome.TwoFactorRequired, null, challenge.ChallengeToken);

        var payload = TryDeserialize<TokenResponse>(body);
        if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
            return AuthResult.Fail("The sign-in service returned an unexpected response.");

        await tokens.SetAsync(payload);
        return AuthResult.Success;
    }

    private static async Task<string?> ReadLockoutMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var error = TryDeserialize<ErrorBody>(body);
        return error is { LockedOut: true } ? error.Error : null;
    }

    private static async Task<string> ReadRegistrationErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var problem = TryDeserialize<ErrorsBody>(body);

        // Password-policy and consent failures come back as a list, and they are the errors a
        // user can actually act on, so they are shown rather than flattened to "failed".
        if (problem?.Errors is { Length: > 0 })
            return string.Join(" ", problem.Errors);

        var single = TryDeserialize<ErrorBody>(body);
        return single?.Error ?? "That did not work. Please check the details and try again.";
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(body,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string CultureInfoName() => System.Globalization.CultureInfo.CurrentUICulture.Name;

    private sealed record ErrorBody(string? Error, bool LockedOut);

    private sealed record ErrorsBody(string[]? Errors);
}

/// <summary>
/// The consent document versions authservice currently requires.
///
/// Fetched rather than hardcoded: registration is rejected unless it accepts the exact
/// versions that instance is configured with, so a policy bump here would otherwise break
/// sign-up until someone remembered to edit a constant in the client.
/// </summary>
public sealed class ConsentVersions(HttpClient http, DevUser devUser)
{
    public sealed record Versions(string Terms, string Privacy, string Cookies);

    // Matches authservice's own defaults, used only when the endpoint cannot be reached — a
    // sign-up attempt that then fails validation gives a far better error than one that
    // cannot even be submitted.
    private static readonly Versions Fallback = new("2026-01-01", "2026-01-01", "2026-01-01");

    private Versions? _cached;

    public async Task<Versions> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        // A dev user has no identity service to ask. The request could only fail, and the
        // fallback it would land on is the same one returned here.
        if (devUser.IsEnabled) return _cached = Fallback;

        try
        {
            _cached = await http.GetFromJsonAsync<Versions>("/api/v1/auth/consents/versions", ct) ?? Fallback;
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException or NotSupportedException)
        {
            _cached = Fallback;
        }

        return _cached;
    }
}

/// <summary>
/// Attaches the bearer token to every API call, and recovers from a 401 once.
///
/// The retry is what turns an expired access token from a dead end into a hiccup. Without it
/// a long generation started before expiry would begin 401-ing mid-poll, and the only way out
/// was the sign-in form.
/// </summary>
public sealed class AuthMessageHandler(TokenStore tokens, DevUser devUser, IServiceProvider services) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (devUser.IsEnabled)
        {
            // No token to attach and none to refresh, so a 401 here is final: it means the API
            // is not in Development and will not take this header as an identity.
            request.Headers.Remove(DevAuth.UserHeader);
            request.Headers.Add(DevAuth.UserHeader, devUser.Name);
            return await base.SendAsync(request, cancellationToken);
        }

        request.Headers.Authorization = tokens.Header;

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Resolved lazily rather than injected: AuthClient owns an HttpClient that this
        // handler is not attached to, and taking it as a constructor parameter would make the
        // two types a dependency cycle at registration time.
        var auth = services.GetRequiredService<AuthClient>();

        if (!await auth.TryRefreshAsync(cancellationToken))
            return response;

        response.Dispose();

        // A request message cannot be sent twice, so the retry is a copy. Only requests whose
        // body can be re-read are retried; anything else falls through as the original 401.
        var retry = await CloneAsync(request, cancellationToken);
        if (retry is null) return await base.SendAsync(request, cancellationToken);

        retry.Headers.Authorization = tokens.Header;
        return await base.SendAsync(retry, cancellationToken);
    }

    private static async Task<HttpRequestMessage?> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var buffered = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(buffered);

            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
