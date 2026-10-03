# Open deviations from the reference architecture

Required by [`architecture-standards`](https://github.com/konradcinkusz/architecture-standards)
§3a: *"a principle whose named violation stays open indefinitely reads as optional"*. Every
row is dated. A fixed deviation has its row deleted; a deliberately accepted one keeps its
row with the reasoning, because an acknowledged deviation is a decision and an
unacknowledged one is drift.

Compliance was last reviewed in full on **2026-08-14**, against §3 of the reference
architecture.

## Open

| Since | Deviation | Principle | Position |
|---|---|---|---|
| 2026-08-14 | **No OTLP traces, metrics or logs.** No service references OpenTelemetry; observability is the ASP.NET Core console logger and nothing else. | §3 "Emits OTLP traces, metrics and logs" | **To fix.** The generation pipeline crosses four services per document, which is exactly the shape that is undebuggable without traces. The largest genuine gap in this list. |
| 2026-08-14 | **No Aspire AppHost.** Services are wired by `docker-compose.yml` locally and by `flyio/*.toml` in deployment; there is no `AddServiceDefaults()` / `MapDefaultEndpoints()`. | §3 "Declared in the AppHost…", "Calls `AddServiceDefaults()`" | **Accepted for now.** Compose and the Fly configs cover both environments today, and adopting Aspire is a restructuring rather than an addition. Revisit alongside the OTLP work — `AddServiceDefaults()` is what would deliver it. |
| 2026-08-14 | **The shared kernel carries DTOs and enums.** `CvMaker.Shared/Contracts.cs` holds the request/response records and `FailureCause`/`JobStatus`. | §3 "The shared kernel holds no entity, DTO, enum…" | **Accepted, with a caveat.** §4 of the same document permits "a shared DTO contract", and this is that: the API and the Blazor client are two halves of one contract. What is genuinely missing is the enforcement — there is no architecture test or CI size check keeping it from growing into a dumping ground. That part should be built. |
| 2026-08-14 | **No `HttpClient` resilience handler.** `RenderClient` and `AgenticClient` set explicit timeouts but carry no standard resilience pipeline, so a transient failure is a failed job rather than a retried call. | §3 "Outbound `HttpClient`s carry the standard resilience handler" | **To fix.** Small: `AddStandardResilienceHandler()` on both registrations. Needs care with the render call, which can legitimately run for most of its 60 s timeout and must not be retried blindly — a retried LuaLaTeX compile is a doubled bill and a doubled queue. |
| 2026-08-14 | **`Program.cs` is not purely a manifest.** `CvMaker.Api/Program.cs` does its own service wiring inline rather than delegating to `ServiceCollectionExtensions`. | §3 "`Program.cs` is a manifest; wiring is in `ServiceCollectionExtensions`" | **To fix.** Cosmetic and cheap; deferred only because it touches the file every other change also touches. |
| 2026-08-14 | **Schema is applied by `MigrateAsync` in `Program.cs`, not in a hosted service.** It runs before the host starts, so a slow migration delays the listener and therefore the platform health check. | §3 "Schema applied by `MigrateAsync` … in a hosted service" | **To fix.** The reason it is inline is real — `GenerationWorker` queries `GenerationJobs` at startup — but the correct answer is a migration hosted service the worker waits on, which is what authservice already does with `MigrationBackgroundService` and `IMigrationCompletionSignal`. |
| 2026-08-14 | **`cvmaker-render-dev` scales to zero while `CvMaker.Api` calls it in-request.** | §3 "`min_machines_running = 1` if another service calls it in-request" | **Accepted, conditionally.** The call goes over `.flycast`, which starts a stopped machine, and the guide permits exactly this trade. The API's render timeout (60 s) has to cover the cold start as well as the compile, which is tighter than it sounds for an image this size. It stops being acceptable if the render image stays at ~3 GB *and* traffic is bursty — the file's own comment says "min 1 in prod", so revisit at launch. |

## Closed by the 2026-08-14 review

Listed once for traceability, then deleted at the next review.

- Token signing was a shared HS256 secret, giving this API the ability to mint tokens for any
  identity. Now validated against authservice's JWKS with no key material held here.
- No secret scanner in CI, which P5 makes mandatory and which matters more now that the
  deployment has an RSA private key.
- No `CODEOWNERS`, so a change to the escaper or the sandbox reviewed like any other diff.
- `/alive` existed only on `CvMaker.Api`; the renderer and the agent plane had `/health` alone.
- The web app's platform health check pointed at `/index.html`, which nginx serves happily
  when the WASM bundle behind it is broken.
- The migration set had drifted from the model, so the API could not start against a fresh
  database at all.
