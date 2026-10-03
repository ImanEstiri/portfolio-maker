# Contributing

## Running the stack

```bash
docker compose up --build
```

That brings up Postgres, Azurite, the renderer, the agent plane, the API on
`http://localhost:5100` and the web client on `http://localhost:8081`.

There is **no authentication** in this configuration, deliberately: the API runs in
`Development` with no `Auth__Authority` and no `Jwt__SecretKey`, so `UserContext` falls
back to an `X-CvMaker-Dev-User` header standing in for a bearer token. The web client is
started with `DEV_USER=dev-user`, so it sends that header and the UI works signed in as
that user. A request with no header at all is `dev-user` too, so the UI and a bare `curl`
see the same data:

```bash
curl http://localhost:5100/api/templates
curl http://localhost:5100/api/profiles                              # what the UI shows
curl -H 'X-CvMaker-Dev-User: me' http://localhost:5100/api/profiles  # a different user
```

To run against a real identity service instead:

1. Run an [authservice](https://github.com/konradcinkusz/authservice) instance that signs
   with an RSA key (`Jwt__PrivateKeyPem`), uses `CvMaker` as both `Jwt__Issuer` and
   `Jwt__Audience`, and lists `http://localhost:8081` in `Cors__AllowedOrigins`.
   `flyio/authservice.fly.toml` shows the full set of settings.
2. On `api`, set `Auth__Authority` to an address of that instance the API container can
   reach. The API finds the key set through the discovery document there, so
   authservice's `Jwt__PublicBaseUrl` has to be reachable from the container as well.
3. On `web`, remove `DEV_USER` and set `AUTH_BASE_URL` to the address your browser uses
   for the same instance.

Step 3 is not optional. Once `Auth__Authority` is set, `api` stops accepting the dev-user
header — even in `Development` — and answers 401 to it, so a client left in dev mode can
neither sign in nor reach anything.

## What CI gates on

| Check | What it proves |
|---|---|
| Build & Test | The solution compiles and the unit tests pass |
| Migrations match the model (a step in Build & Test) | A model change has a matching migration. Without one, the API cannot start against a fresh database at all |
| Images build; renderer sandbox holds | The images build, and the renderer resists hostile input under the same container hardening `docker-compose.yml` applies |
| PDF from hand-entered data | A profile typed in by hand becomes a real PDF, through the same queue, worker, binder and renderer the AI path uses |
| Secret scan | gitleaks finds no credential anywhere in the git history, not just the latest commit |

Two of those deserve emphasis:

**The escaper tests are a security gate.** `LatexEscaperTests` holds an injection corpus.
A failure there means untrusted text can reach the TeX engine as a command. Do not
"fix" it by adjusting the expectation.

**The sandbox proofs bypass the escaper on purpose.** They feed `\input{/etc/passwd}`,
`\write18{...}` and an expansion bomb straight to the renderer to prove the second layer
holds if the first ever fails. Both layers are load-bearing; neither is a backup for the
other.

## Changing the model

EF Core 9 refuses to migrate a model with pending changes, so a model change without a
migration is a hard startup failure rather than a latent inconsistency:

```bash
dotnet ef migrations add <Name> --project src/CvMaker.Api
```

CI checks this with `has-pending-model-changes`.

## Conventions

- Comments explain **why**, not what. If a line needs a comment to say what it does, it
  usually wants rewriting instead.
- Behaviour changes come with a test. For anything touching the escaper or the sandbox,
  that means a case in the corpus.
- Configuration comes from the environment. No secret in source, in a committed
  `appsettings.json`, or in a comment.
- One logical change per pull request.

## Secrets, locally

Never in the working tree. Use `dotnet user-secrets` for the API, or a gitignored
`.env`. `.gitignore` covers `.env`, `*.pem` and `*.key`; if you find yourself wanting to
commit a credential "just for testing", that is the signal to stop.

`./scripts/setup.sh` checks your prerequisites and installs a pre-commit hook that runs the
same gitleaks scan CI does, over what you are about to commit. The hook refuses to commit
when no scanner is available, rather than warning and carrying on — install
[gitleaks](https://github.com/gitleaks/gitleaks#installing) or run Docker. `./scripts/scan-secrets.sh`
runs the full-history scan locally, exactly as CI does. If a real secret ever does land in
history, rotate it first and clean the history second: it is public the moment it is pushed.
