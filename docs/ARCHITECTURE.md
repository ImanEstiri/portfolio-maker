# Architecture

How cv-maker is put together, and why the lines are drawn where they are. The
[README](../README.md) covers running and releasing it and lists what is not built;
[SECURITY.md](../SECURITY.md) states the claims worth attacking; this page explains the
structure those documents rely on. [architecture.mmd](../architecture.mmd) draws it, and the
reasoning behind individual choices is in [docs/decisions](decisions/).

## Product shape

A user enters a profile once, as *facts* — one achievement, job, degree or skill each —
optionally pastes a job posting, picks a template and gets a typeset PDF. They never see
LaTeX. There are two routes to a document:

- **Untailored**, with no posting: `ProfileContentBuilder` groups the facts into sections and
  the PDF is built from them directly. No model is involved and no quota is used.
- **Tailored**, with a posting: an agent flow reorders and rewords the facts for that posting,
  and a critic reports how many of the posting's keywords the result covers. This route costs
  model tokens, so it is metered ([Quota](#quota)).

A manual workflow — paste an offer into an AI assistant, review the generated `.tex` in a pull
request, compile it in CI — works for one author who reads the diff. A service for strangers
cannot rely on that review, so model output is constrained to data and typesetting happens in
a sandbox ([The two rules](#the-two-rules)).

## Services and why they are separate

| Project | Role | Public listener on Fly |
|---|---|---|
| `CvMaker.Web` | Blazor WebAssembly client served by nginx | yes |
| `CvMaker.Api` | Minimal APIs, job queue and worker, quota, erasure; owns the database | yes |
| `CvMaker.Agentic` | Flow engine and agents (Azure AI Foundry, or a deterministic mock) | no |
| `CvMaker.Render` | LaTeX to PDF in a TeX Live container | no |
| `CvMaker.Core` | Profile model, fact gate, LaTeX escaping, template binding; no I/O | library |
| `CvMaker.Shared` | DTOs, enums and the agent contract shared by API, agents and client | library |

- **Render is a service, not a library.** It is the only component that runs a process driven
  by user text and the only one that needs a multi-gigabyte TeX installation, so keeping it out
  of the API keeps that image small and lets the renderer be sized and locked down on its own.
  It has no database, authentication or model credentials.
- **Agentic is a service** so the model backend sits behind one internal HTTP interface that
  only the API calls, with its own timeouts, scaling and exposure. It is not a credential
  boundary: the Fly deployment gives the API and the agent plane the same Azure service
  principal ([README](../README.md#release--deployment)).
- **Core has no I/O.** Escaping and binding are where a bug is a broken PDF or a security
  incident, so they sit in a project whose tests need no infrastructure. Reading templates from
  disk is the API's job (`TemplateCatalog`).

The Api/Core/Shared/Web split, the channel-backed queue with a background worker and the
Blazor-behind-nginx client follow [black-hole-sim](https://github.com/konradcinkusz/black-hole-sim).

## The two rules

**The agent never writes LaTeX.** Agents return JSON that deserialises into `ComposedCv`
(`CvMaker.Shared`), a type with no markup fields: every value is a plain string.
`CvGenerationOrchestrator` accepts it only if it parses and is structurally sound; anything
else fails the job with `SchemaValidationFailed` — there is no repair prompt and no fallback to
free text. `TemplateBinder` then escapes every value before the template engine sees it, so
model text can only ever be literal text on the page. Templates are repository files; no user
or model text is ever a template. The contact block (name, headline, email, phone, location,
links) is copied from the profile, never from model output.

**Every claim cites a fact.** Every summary, entry heading and bullet carries `sourceFactIds`.
`FactReferenceValidator` rejects content in which any of them is empty or names a fact id that
is not in the profile, and the job fails with `FactValidationFailed` instead of producing a
PDF. The same validator runs on both routes: the untailored route cites each fact it copies, so
it passes by construction rather than by exemption. The gate checks *citations*. It does not
compare an entry's organisation, dates or wording with the facts it cites, so it proves that a
claim points at something the user wrote, not that the claim agrees with it. The composer's
instructions forbid new employers, titles, dates and numbers, but that is a prompt, not a
check. SECURITY.md draws the same line between provenance and truth.

## The agent layer

Agents are data. `src/CvMaker.Agentic/agents/<slug>/definition.json` holds a slug, version,
model (`gpt-4.1`), temperature and instructions, so a prompt change is a reviewable diff with a
version number. Five definitions ship; the flow uses `posting-analyst`, `cv-composer` and
`ats-critic`, and no route reaches `profile-extractor` or `cover-letter-writer`.

A flow is data too. `flows/cv-generation-flow.json` runs `analyse-posting`, `compose-cv` and
`critique`, then a conditional on the critic's verdict that either skips to `done` or runs
`revise` — the composer again, given the critique — so there is at most one revision.
`FlowEngine` implements only what this flow uses: `agent-call`, `conditional` (equality against
a field of an earlier step's JSON output) and `noop`, with `{{input.*}}` and `{{steps.*}}`
interpolation. Unresolved placeholders become empty strings, code fences around JSON are
stripped, and the posting is wrapped in the prompt as untrusted data.

The API calls `POST /api/compose/cv` with a `ComposeRequest` — posting text, the profile's
facts, the template's column layout and page target — and gets `200` with a `ComposeResult`
either way: the content and the critic's `AtsReview`, or a failure carrying a `FailureCause`.
The review assesses the first draft; it is not re-run after a revision.

`IAgentRuntime` has two implementations. `FoundryAgentRuntime` creates each agent in Azure AI
Foundry on first use (cached per slug and version for the life of the process) and runs every
invocation on a fresh thread, deleted afterwards even on failure, because it holds the pasted
posting and the facts. `MockAgentRuntime` is used when no Foundry endpoint is configured: it
answers deterministically, and every bullet it composes cites a real fact id, so the pipeline
runs offline and the fabrication cases are tested with hand-written payloads instead.
`GET /api/runtime` reports which backend is live. Each definition declares a `temperature`
that the Foundry runtime does not apply.

## Templates

A template is a directory under `templates/`: `template.json` (id, display name, description,
engine, ATS rating, `capabilities`, `pageBudget`, `assets`), `cv.tex.scriban` and an `assets/`
folder for support files such as a document class. `TemplateCatalog` reads them once, when it
is first needed, and keeps them in memory; a malformed manifest or a declared asset that is
missing makes that first use fail.

`TemplateBinder` renders them with Scriban in strict mode (a misspelt variable is an error, not
an empty string), a loop limit of 5,000 and a recursion limit of 50. A template sees only the
escaped view model, with a length cap per slot (600 characters for a bullet, 200 for a name or
heading). The worker tells the composer whether the template is two-column and its page target.
Support files travel to the renderer with each request, under a name and extension allowlist.

Two templates ship. `ats-plain` is single-column, uses only base LaTeX packages and is rated
"excellent" for applicant tracking systems; the UI selects it by default. `altacv-modern` is
two-column, rated "fair", and built on the vendored `altacv.cls`, which is LPPL rather than MIT
([THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)). The end-to-end CI job compiles both.

## Threat model and controls

Profile text, pasted postings and the model output derived from them are attacker-controlled.
An attacker wants code execution or file disclosure through the TeX engine, resource exhaustion
in the renderer, a PDF whose extractable text differs from what it shows, another user's data,
or free model spend. Controls, by layer:

| Layer | Controls |
|---|---|
| Input (`CvMaker.Core`) | One-pass escaping of every TeX metacharacter; invisible, bidi-override and control characters stripped; whitespace folded; per-slot length caps; link targets limited to `http`, `https` and `mailto` |
| Model output | JSON only, parsed and checked; the fact gate; contact details taken from the profile |
| Compiler (`CvMaker.Render`) | `latexmk -no-shell-escape -halt-on-error`; `openin_any=p`, `openout_any=p` and `shell_escape=f` in the environment; a fresh work directory per compile, removed afterwards; a wall-clock limit (30 s by default) with a process-tree kill; caps on source (2 MB) and PDF (20 MB) size; allowlists for the engine and for support-file names; a non-root user |
| Container | Read-only root, all capabilities dropped, no-new-privileges and a tmpfs work directory under docker-compose and in CI, **not** on Fly. Egress is not denied anywhere |
| API | Ownership is checked on every read and another user's resource is a `404`, not a `403`; document creation is limited to 5 a minute per user; request bodies are size-capped; CORS admits only configured origins |
| Data | Pasted postings are deleted after 30 days and `DELETE /api/me/data` erases the rest; the storage account has no anonymous access and shared-key authentication is disabled |
| Browser | Access token in memory, refresh token in `sessionStorage` (`TokenStore`); the content security policy is written at container start so `connect-src` names only the API and auth origins ([ADR 0001](decisions/0001-auth-ui-lives-here.md)) |

CI proves the escaper with an injection corpus (`LatexEscaperTests`) and the sandbox with
payloads that deliberately bypass the escaper, so each layer is shown to hold alone.
[SECURITY.md](../SECURITY.md) lists what is not solved, prompt injection first.

## The job pipeline

`POST /api/documents` is rate limited. It checks the template and that the profile is the
caller's, consumes a quota unit if there is a posting, stores the posting (stamped to be purged
in 30 days), a `Document` and a `GenerationJob` in state `Pending`, saves, and only then
enqueues the job id and returns `202`. The client polls `GET /api/jobs/{id}` every two seconds
and downloads `GET /api/jobs/{id}/pdf`, which streams the file from blob storage through the API.

`ChannelGenerationJobQueue` is an in-memory bounded channel (100 jobs, one reader), so queued
work does not survive a restart: `flyio/api.fly.toml` keeps a machine running and the worker
recovers jobs at startup. `GenerationWorker` runs up to `Jobs:MaxConcurrency` jobs at a time
(default 2). Each job records its stage — `queued`, `composing`, `tailoring` (only with a
posting), `typesetting`, `uploading`, then `done`, `failed` or `cancelled` — while it builds the
content (agent flow or `ProfileContentBuilder`), runs the fact gate, binds, renders and stores
the PDF as `{userId}/{jobId}/cv-{jobId}.pdf` in the `generated-cvs` container. If the tailoring
flow fails, so does the job: it does not fall back to an untailored CV the user might send
without noticing. Every failure site records a `FailureCause`.

### Recovery and cancellation

At startup the worker re-enqueues every `Pending` or `Running` job; running ones are reset to
`Pending` and stamped `WorkerRestart`, which is cleared when the job runs again. Recovery runs
alongside the consumer loop, not before it, so a backlog larger than the queue cannot deadlock
startup. `DELETE /api/jobs/{id}` marks the row `Cancelled` and signals `JobCancellationRegistry`,
which stops the job if it is running; a job cancelled while queued is skipped when the worker
picks it up, and a finished job answers `409`.

### Quota

Only tailored generations are metered. `QuotaService` keeps one `UserQuotas` row per user over a
period that rolls 30 days from first use (3 units by default, `Quota:Free`). A tailored request
consumes a unit with a single conditional `UPDATE ... WHERE used < limit`, so concurrent
requests cannot share one; when none is left the API answers `402`. The unit is consumed when
the request is accepted, before any model call, and a failed or cancelled job does not give it
back ([ADR 0004](decisions/0004-quota-and-refunds.md)).

### Retention and erasure

`PostingPurgeService` deletes postings whose `PurgeAt` has passed, hourly by default and in
batches; a document outlives the posting it was tailored from. `GET /api/me/data` counts what
the service holds for the caller. `DELETE /api/me/data` removes profiles (and their facts),
postings, documents and jobs in one transaction, then deletes the PDF blobs on a best-effort
basis. It keeps the quota row, because deleting it would reset the allowance. The sign-in
account lives in authservice and is not touched (README, [Status](../README.md#status)).

## Data model

Everything is in the `cvmaker` database. The API applies migrations at startup in every
environment, because the worker queries `GenerationJobs` as soon as the host starts, and CI
fails if the model and the migrations disagree. Users are not a table: `UserId` is the token's
`sub` claim as an opaque string, and nothing joins to the identity database.

| Table | Holds | Notes |
|---|---|---|
| `Profiles` | name, headline, email, phone, location, `Links` (jsonb) | indexed on `UserId` |
| `ProfileFacts` | `FactId`, `Kind`, `Text`, organisation, role, location, start and end date | `FactId` unique within a profile; deleted with it — the only foreign key in the schema |
| `JobPostings` | pasted `RawText` (up to 60,000 characters), `CreatedAt`, `PurgeAt` | indexed on `PurgeAt` for the sweep |
| `Documents` | `ProfileId`, `PostingId` (null on the untailored route), `TemplateId`, `Kind`, `Version` | plain columns, so a document outlives its posting; `Version` is always 1 |
| `GenerationJobs` | `Status`, `Stage`, `Progress`, `Error`, `FailureCause`, `ContentHash`, `AtsScore`, `AtsReviewJson` (jsonb), `PdfBlobPath` | indexed on `UserId` and `Status`; only the PDF's path is stored |
| `UserQuotas` | `Tier`, `TailoredUsed`, `TailoredLimit`, `PeriodStart` | unique on `UserId`, which is what makes the first-use race safe |

Enums are stored as integers and serialised by name on the wire. Nothing reads `ContentHash`
(SHA-256 of the bound `.tex`): there is no render cache.

## Deployment shape

Each service is one Fly app in `fra` (`flyio/*.toml`, named `cvmaker-<service>-dev`).

- **Public:** `web` (nginx, scales to zero), `api` (one machine always running, because the
  queue is in memory) and the `authservice` instance, which cv-maker deploys from a pinned
  published image but does not build.
- **No public listener:** `render` and `agentic`, reached from the API over Fly's private
  network (`.flycast` addresses), and `postgres` (one volume-backed server holding two
  databases, [ADR 0002](decisions/0002-one-postgres-two-databases.md)). Private is not isolated:
  anything on the Fly organisation's private network can reach them ([SECURITY.md](../SECURITY.md)).
- **Azure** (`infra/main.bicep`, provisioned by the release workflow): an Azure OpenAI account
  with a `gpt-4.1` deployment, an AI Foundry hub and project, and a storage account with a
  private `generated-cvs` container. Fly has no managed identity, so services authenticate with
  one service principal through `DefaultAzureCredential`. Where Azure processes a prompt follows
  from the `AZURE_LOCATION` you deploy to and the deployment SKU (`GlobalStandard`); this
  repository does not pin it to a region.
- **Local:** `docker-compose.yml` runs the same images against Postgres, Azurite and the mock
  agent runtime, with no Azure account. The end-to-end CI job drives that stack
  ([CONTRIBUTING.md](../CONTRIBUTING.md#what-ci-gates-on)).

## Identity

cv-maker has no identity code. `CvMaker.Api` validates bearer tokens against the key set an
authservice instance publishes and holds no signing key, and `CvMaker.Web` signs users in through
that instance's HTTP API, with the account screens living in this repository
([README, Authentication](../README.md#authentication); [ADR 0001](decisions/0001-auth-ui-lives-here.md)).

Every user endpoint requires a valid bearer token outside Development, and also in Development
whenever token validation is configured (`Auth:Authority` or `Jwt:SecretKey`). The
`X-CvMaker-Dev-User` header is honoured only in Development with no token validation configured,
which is what lets `docker compose up` run without an identity service. The template catalogue
and the health endpoints are public.

## Decisions

- [0001](decisions/0001-auth-ui-lives-here.md) — The account screens live in cv-maker
- [0002](decisions/0002-one-postgres-two-databases.md) — One Postgres app, two logical databases
- [0003](decisions/0003-bring-your-own-key.md) — Free tier: bring your own model key
- [0004](decisions/0004-quota-and-refunds.md) — Quota is charged on acceptance; failures are not refunded

What is not built is listed under *Known gaps* in the README ([Status](../README.md#status)).
