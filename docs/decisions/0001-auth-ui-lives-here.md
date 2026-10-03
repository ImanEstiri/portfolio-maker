# ADR 0001 — The account screens live in cv-maker

**Status:** Accepted
**Date:** 2026-08-14
**Amended:** 2026-10-03 — the description of authservice's scope was made precise; the
decision is unchanged.

## Context

cv-maker had a sign-in form and nothing else. No registration, no password reset, no social
sign-in, and no link to any of them. A stranger could read the landing page, list the public
templates, and stop — everything past that needed a token and there was no way to obtain one.

authservice provides registration, password reset, social login and versioned consent as an
HTTP API. It ships no registration, sign-in or password-reset screens for a web application,
and its own [scope decision](https://github.com/konradcinkusz/authservice/blob/main/docs/decisions/0003-scope.md)
is to stay small: no admin UI, no OpenID Connect provider.

So the question was where the screens live: built here against that API, or redirected to
pages hosted by authservice.

## Options

**A. Build them here.** cv-maker gets its own Register / Login / Forgot / Reset / Two-factor
pages, posting to authservice. One product with one visual language. Credentials are typed on
this origin.

**B. Redirect to authservice-hosted pages.** Far less code here, and credentials never touch
this origin. The cost is a visible seam between two products mid-signup — and, decisively,
those pages do not exist for this case: choosing B means building a UI inside a service whose scope
decision is to stay small.

## Decision

**A.** authservice stays a pure API.

B's real advantage — credentials never entering this origin — is smaller than it appears for a
WebAssembly app. The token still has to come back to this origin and be held by this
JavaScript context, so a compromise of this origin is serious either way; B narrows the window
rather than closing it. Set against building and maintaining a second UI in a service whose
scope decision is to stay small, that is not worth it.

## Consequences

- **A password field on this origin is now a security-relevant surface.** The CSP is not
  decoration: `form-action 'self'`, `base-uri 'self'` and a `connect-src` naming only the API
  and auth origins are what stop injected script posting those credentials elsewhere.
- **Consent is captured here** and recorded by authservice against the version accepted. The
  versions are fetched from authservice rather than hardcoded, so bumping a policy version
  cannot silently break sign-up.
- **Social sign-in is possible but not yet built.** The blocker was that authservice's OAuth
  callback put the refresh token in a redirect query string, which lands in browser history and
  in `Referer`. Its callback now returns a single-use exchange code instead, redeemed with
  `POST /api/v1/external-auth/exchange`. The exchange call is small; the button is not there
  yet.
- **Every authservice error message is now user-facing copy in this app**, including the
  deliberately vague ones. Password reset says the same thing whether or not the account
  exists, because saying otherwise is an account-enumeration oracle.
