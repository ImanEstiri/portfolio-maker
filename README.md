<a name="readme-top"></a>

# cv-maker

**Tailored CVs, as a web app.** Paste a job posting, enter your history once,
pick a template — get a typeset PDF. The user never sees LaTeX, and never
learns it was involved.

[![Ask me anything](https://flat.badgen.net/static/Ask%20me/anything?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz "Ask me anything")
[![GitHub license](https://flat.badgen.net/github/license/konradcinkusz/portfolio-maker?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/blob/main/LICENSE "GitHub license")
[![Maintained](https://flat.badgen.net/static/Maintained/yes?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/commits/main "Maintained")
[![GitHub branches](https://flat.badgen.net/github/branches/konradcinkusz/portfolio-maker?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/branches "GitHub branches")
[![GitHub commits](https://flat.badgen.net/github/commits/konradcinkusz/portfolio-maker?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/commits "GitHub commits")
[![GitHub issues](https://flat.badgen.net/github/issues/konradcinkusz/portfolio-maker?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/issues "GitHub issues")
[![GitHub pull requests](https://flat.badgen.net/github/prs/konradcinkusz/portfolio-maker?icon=github&color=black&scale=1.01)](https://github.com/konradcinkusz/portfolio-maker/pulls "GitHub pull requests")

[![CI](https://github.com/konradcinkusz/portfolio-maker/actions/workflows/ci.yml/badge.svg)](https://github.com/konradcinkusz/portfolio-maker/actions/workflows/ci.yml "CI")
[![Secret scan](https://github.com/konradcinkusz/portfolio-maker/actions/workflows/secret-scan.yml/badge.svg)](https://github.com/konradcinkusz/portfolio-maker/actions/workflows/secret-scan.yml "Secret scan")

---

## Run it

The whole stack, including a working LaTeX toolchain, with no Azure account
and no API keys:

```bash
docker compose up --build
# UI              http://localhost:8081
# API (swagger)   http://localhost:5100/swagger
```

With no `AzureAIFoundry__Endpoint` configured, the agent service runs on a
deterministic mock runtime, so the full pipeline — profile → tailoring →
binding → typesetting → PDF — works end to end offline. `GET /api/runtime` on
the agent service reports which backend is live, so a misconfigured deployment
cannot quietly serve mock CVs.

Authentication is not part of this compose file — see
[Authentication](#authentication) below. Without it, the API stays in
`Development`, where `UserContext` takes the caller from an
`X-CvMaker-Dev-User` header in place of a bearer token, and the web client is
started with `DEV_USER=dev-user` so that it sends one. The UI therefore comes up
signed in as a development user, with a **Dev mode** badge where "Sign out"
would be. Only an API running in `Development` with no token validation
configured accepts that header; anywhere else — including a `Development` API
that has `Auth__Authority` set — the same client gets a 401.
[CONTRIBUTING.md](CONTRIBUTING.md) covers switching to real sign-in.

## Where it comes from

cv-maker productises a workflow that works for one person with an IDE and for
nobody else: find an offer, paste it into an AI assistant, get a new `.tex` file,
and let CI compile the PDF. The app keeps what carries over — typesetting from
repo-owned LaTeX templates, so a build cannot be broken by model output — and
replaces what cannot: the assistant produces structured JSON instead of LaTeX,
and a binder escapes and typesets it. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
has the design.

## Shape

.NET 9. The layout follows two earlier projects,
[`black-hole-sim`](https://github.com/konradcinkusz/black-hole-sim) and
[`copilot-scope`](https://github.com/konradcinkusz/copilot-scope): an
Api/Core/Shared/Web split, a `Channel`-backed job queue with a background worker,
EF Core + Npgsql. The agent layer is JSON agent definitions run by a small flow
engine. One Fly.io app per service.

| Project | Does |
|---|---|
| `CvMaker.Core` | Profile model, template binding, **LaTeX escaping**. No I/O — tests run in milliseconds |
| `CvMaker.Api` | CRUD, job queue and worker, quota, data purge |
| `CvMaker.Agentic` | Azure AI Foundry agents + JSON flow engine, with a mock runtime |
| `CvMaker.Render` | LaTeX → PDF in a sandboxed TeX Live container |
| `CvMaker.Web` | Blazor WebAssembly behind nginx |
| `CvMaker.Shared` | Contracts shared with the client |

There is no `CvMaker.Auth` — accounts, tokens and consent are owned by the
external [authservice](https://github.com/konradcinkusz/authservice); see
[Authentication](#authentication).

## Authentication

cv-maker has no identity code of its own. It authenticates against
[konradcinkusz/authservice](https://github.com/konradcinkusz/authservice), a
standalone ASP.NET Core auth service (JWT access/refresh tokens, social
login, versioned GDPR consent, multi-tenant orgs) that is built and released
from its own repository. What cv-maker owns is its *instance*: authservice
publishes an image and never deploys itself, so `flyio/authservice.fly.toml`
pins a version of that image and `flyio.yml` deploys it — with its own
database and its own signing key — whenever that file changes.

The integration is config-only, on both sides:

- **`CvMaker.Web`** calls whatever `AuthBaseUrl` (`wwwroot/appsettings.json`,
  or the `AUTH_BASE_URL` env var baked in at container start — see
  `src/CvMaker.Web/docker-entrypoint.sh`) points at, hitting
  `/api/v1/auth/login` — authservice's actual route — via
  `Services/TokenStore.cs`'s `AuthClient`.
- **`CvMaker.Api`** validates the bearer token against authservice's published
  key set (`Auth/JwtExtensions.cs`). `Auth:Authority` names the instance; the
  API finds the key set through its discovery document
  (`/.well-known/openid-configuration`) and holds **no key material at all**,
  so it can verify a token and cannot mint one.

  That asymmetry is the point. Under the previous HS256 arrangement the
  validation key was also the signing key, which handed a CV renderer the
  ability to issue tokens for any user with any role, including
  administrators of the identity system. The shared secret still works via
  `Jwt:SecretKey` for a deployment mid-migration, and the API says so in its
  startup log.

  `Jwt:Issuer` and `Jwt:Audience` must match the authservice instance and are
  `CvMaker`/`CvMaker` here — not authservice's `AuthService` default, or a
  token from either product would authenticate against the other.

In short: stand up (or point at an existing) authservice instance, give it an
RSA signing key and set its `Jwt:Issuer`/`Jwt:Audience` to `CvMaker`, then hand
that issuer/audience and that instance's URL to `cvmaker-api-dev`, and its URL to
`cvmaker-web-dev` — see `flyio/authservice.fly.toml`, `flyio/api.fly.toml` and
`flyio/web.fly.toml` for the exact secret/env names.

## Two rules the design is built around

**The agent never writes LaTeX.** Agents emit schema-validated JSON; a .NET
binder renders it into repo-owned templates with every value escaped before the
template engine sees it. Model output can never form a control sequence — which
is both why the build cannot break unattended and why a pasted job posting
cannot become remote code execution.

**Every claim cites a fact.** Every bullet, entry header and summary must cite
the `factId` of the profile item it came from. Content with a missing or unknown
citation is rejected and the generation fails closed, rather than shipping a PDF
whose claims point at nothing. The gate checks *citations*, not agreement: it
does not compare an entry's organisation, dates or wording with the fact it
cites, so it proves that a claim points at something the user wrote, not that
the claim matches it. The composer's instructions forbid new employers, titles,
dates and numbers, but that is a prompt, not a check. The hand-entered path
satisfies the gate by construction, so it runs on the real pipeline rather than
only guarding the AI route.

## Security posture

The renderer runs a process driven by untrusted input, so it gets two
independent layers and CI proves each separately:

- **Escaping**, in `CvMaker.Core` — single-pass over every TeX metacharacter,
  plus stripping of bidi overrides and zero-width characters, which would
  otherwise let a PDF display one employer while the text an ATS extracts says
  another.
- **Sandbox**, in `CvMaker.Render` — no shell escape, `openin_any`/`openout_any`
  restricted, per-compile work directory, hard timeout with a process-tree kill,
  non-root, no public IP. Under `docker-compose.yml` and in CI the container is
  also read-only with every capability dropped; the Fly config does not apply
  those flags (SECURITY.md has the detail, and what is not denied yet).

CI runs an injection corpus as unit tests, then starts the render image under
the same container hardening `docker-compose.yml` applies and confirms that file
reads outside the work directory are blocked, `\write18` does not execute, and
an expansion bomb dies at the timeout.

## Release & deployment

Pushing a `v*` tag (equivalently, publishing a GitHub Release against a `v*`
tag) triggers `.github/workflows/flyio.yml`, which:

1. **Provisions Azure resources** (`provision-azure` job → `infra/main.bicep`):
   an Azure OpenAI resource with a `gpt-4.1` deployment, an Azure AI Foundry
   Hub + Project connected to it, and a private Blob Storage account with a
   `generated-cvs` container — the exact resources `CvMaker.Agentic`'s
   `FoundryAgentRuntime` and `CvMaker.Api`'s `AzureBlobArtifactStorageService`
   already expect. All RBAC is granted to a single service-principal object
   ID, since Fly.io has no managed identity and every service authenticates
   via `DefaultAzureCredential`. The deployment is idempotent, so re-running
   it just confirms the resources are still there.
2. **Detects which apps changed** since the previous tag and deploys only
   those, in dependency order: `CvMaker.Render`, `CvMaker.Agentic` and
   authservice first, then `CvMaker.Api` (which calls the first two over Fly's
   private network and validates every token against authservice's key set),
   then `CvMaker.Web`. `deploy-agentic` and `deploy-api` also `flyctl secrets set`
   the Foundry endpoint / storage account URL from step 1, plus the
   `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` service-principal
   credentials both need for `DefaultAzureCredential`.

   authservice is built elsewhere, so the only thing here that can change it is
   `flyio/authservice.fly.toml` — typically a bump of the pinned image version.
   Its secrets (the database connection string and the RSA private key,
   `Jwt__PrivateKeyPem`) are set once by an operator, never by CI, and the
   deploy fails if the key set it publishes comes back empty.
3. Leaves Postgres (`flyio/postgres.fly.toml`) alone — a data-holding database
   is not something a cv-maker tag should ever touch.

Required repository secrets: `FLY_API_TOKEN`; `AZURE_CREDENTIALS` (a JSON
blob — `{ clientId, clientSecret, tenantId, subscriptionId }` — for
`azure/login@v2`); `AZURE_SUBSCRIPTION_ID`. Required/optional repository
variables: `AZURE_LOCATION` (e.g. `eastus2`), `AZURE_ENV_NAME` (optional,
defaults to `cvmaker-flyio-dev`).

## Status

Implemented: the escaper and fact gate, the template binder, the sandboxed
renderer, profiles/documents/jobs, the generation flow (posting analyst → CV
composer → ATS critic, plus at most one revision when the critic asks for it),
quota, data purge, the Blazor client, external-auth integration, and Fly
configs plus Azure provisioning for every service that needs Azure.

CI runs three gating jobs: **Build & Test** (which also asserts the migration
set matches the model), **Images build; renderer sandbox holds**, and **PDF
from hand-entered data** (profile → document → PDF against a live compose
stack, for both catalogue templates, plus the CORS preflight and job
cancellation). The CI badge above shows their current state. A separate
workflow scans the full git history for secrets.

Known gaps, each called out at its call site as well as here:

- **Generated PDFs are downloaded through the API, not via signed URLs.**
  `AzureBlobArtifactStorageService` stores them in the `generated-cvs`
  container, but nothing yet issues a time-limited SAS URL for direct client
  download — every download proxies through `CvMaker.Api`.
- **Closing an account is not self-service.** "Your data" erases everything this
  service holds, but the sign-in account lives in authservice and nothing in
  this app deletes it yet — so erasure leaves a login with no data behind it,
  never data with no login to reach it.
- **Malformed model output is not retried.** If the composer's reply does not
  parse, the generation fails — and, because a tailored request is metered when
  it is accepted, still uses one of the user's generations
  ([ADR 0004](docs/decisions/0004-quota-and-refunds.md)). One re-prompt with the
  parser's error would remove most of those failures.
- **Cover letters, multi-language and version history** are not built.
  Multi-language is template work, not a prompt change. `ats-plain` loads T1
  `fontenc` with Latin Modern and `altacv-modern` sets Roboto Slab and Lato
  through `fontspec`; CI compiles neither with accented or non-Latin text, and
  Cyrillic or CJK would need fonts that carry those glyphs in both.
- **The render image is ~3 GB**, because `altacv.cls` needs `tikz`, `tcolorbox`,
  `academicons` and friends. Slimming it with `tlmgr` is the known fix and is
  not done.

## Documents

- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — how it is put together and
  why: service boundaries, the two rules, the agent and template contracts, the
  threat model, the job pipeline and the data model.
- **[docs/decisions/](docs/decisions/)** — the decisions behind it, as ADRs,
  each with what was rejected.
- **[architecture.mmd](architecture.mmd)** — the diagram.
- **[docs/architecture/DEVIATIONS.md](docs/architecture/DEVIATIONS.md)** — where
  the code departs from the reference architecture it is measured against, and
  whether each departure is accepted or to be fixed.
- **[SECURITY.md](SECURITY.md)** — the claims worth attacking, how to report one,
  and what is not claimed.
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — running the stack, what CI gates on,
  and why the escaper tests are a security gate.
- **[LICENSE](LICENSE)** (MIT) and
  **[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)** — the vendored
  `altacv.cls` is LPPL, not MIT.
