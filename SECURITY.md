# Security policy

## Reporting a vulnerability

Email **konradcinkusz@gmail.com** with "cv-maker security" in the subject. Please do not
open a public issue for anything exploitable.

Include what you did, what happened, and what you expected. A minimal reproduction — the
input, the request, the response — is worth more than a description.

**What to expect:** an acknowledgement within 3 working days, an assessment within 10.
This is a personal project with no security team behind it; that is the honest response
time, not an SLA. You will be credited in the fix unless you prefer otherwise.

## What this project claims

These are specific, testable claims. They are also the things worth attacking, so they
are stated plainly rather than implied:

1. **Untrusted text never reaches the TeX engine as a command.** Everything a user types
   or pastes is escaped before it becomes LaTeX. The corpus in
   `tests/CvMaker.Tests/LatexEscaperTests.cs` is a security gate, not a style check — a
   failure there is a regression in the boundary, and CI treats it that way.

2. **The compiler is sandboxed, and that is a second layer rather than the first.** In
   every environment the renderer runs as a non-root user, with shell escape disabled,
   file access confined to a fresh per-compile work directory, and a hard compile
   timeout. Under `docker-compose.yml` and in CI the container also runs with a read-only
   root filesystem, all capabilities dropped, no new privileges and a tmpfs work
   directory; `flyio/render.fly.toml` sets none of those container flags, so on Fly the
   sandbox is the in-process controls, the non-root user and the machine's own VM. CI
   proves the sandbox against hostile input that deliberately bypasses the escaper —
   `\input{/etc/passwd}`, `\write18{...}`, and an expansion bomb. Both layers are
   load-bearing.

3. **The compile plane is not reachable from the internet.** The renderer and the agent
   plane have no public listener. Under `docker-compose.yml` only the other containers
   can reach them; on Fly, anything on the organisation's private network can, which is
   every app in that organisation, not only the API.

4. **Generated content cites the user's own facts.** Every summary, entry heading and
   bullet must cite a fact in the profile, and content with a missing or unknown
   citation is rejected rather than typeset. That is the whole check. It proves a claim
   *points at* something the user wrote, not that the claim *agrees with* it: an entry's
   organisation, location and dates, and a bullet's wording, are not compared with the
   fact they cite, so a model that cites a real fact while inventing an employer or a
   number passes the gate. Forbidding that is the composer prompt's job, not the
   validator's. Nor can the check tell you whether what you typed is accurate.

5. **This service cannot mint authentication tokens.** It validates them against the
   identity service's published JWKS and holds no signing key.

## Known posture

- **No external security audit.** Nobody independent has reviewed this.
- **Prompt injection is not solved.** A pasted job posting reaches an agent prompt
  as untrusted text. The citation check above does not bound the damage the way it
  sounds like it should: it verifies that a citation exists, not that the text agrees
  with it, so an injected instruction that gets a model to write something false or
  unwanted under a real citation is not caught. What does hold is that whatever the
  model returns is escaped as plain text (claim 1), and that the contact details on
  the CV come from the profile, not from the model.
- **The renderer's TeX distribution is large**, and therefore has a large dependency
  surface. Dependabot watches the base images.
- **Egress is not denied.** Nothing yet stops the renderer from opening outbound
  connections, under compose or on Fly. The escaper and the in-process controls are what
  keep hostile input from getting that far; a network namespace around each compile is the
  stronger version, described in `src/CvMaker.Render/Dockerfile`.
- **Deleting your data here does not delete your account**, which lives in the
  separate identity service.

## Out of scope

- Findings against `docker-compose.yml`'s deliberately weak local credentials. They
  are for evaluation and documented as such.
- Missing hardening headers with no described attack path.
- Scanner output with no analysis of whether the finding is reachable here.
- Denial of service by generating many documents. Quotas bound it; abuse is a
  fair-use matter, not a vulnerability.
