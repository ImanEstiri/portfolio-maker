# ADR 0003 — Free tier: bring your own model key

**Status:** Accepted, **not yet implemented**
**Date:** 2026-08-14

## Context

Tailoring a CV to a posting costs real money per document, paid to a model provider. Before
pointing an audience at this, someone has to decide who pays and what bounds the bill.

## Options

**A. Small free tier, the operator pays.** Simplest to use — sign up and generate. The spend
ceiling is (active users × documents each × per-document cost), which is unbounded in the one
variable nobody controls.

**B. Invite-only.** Tightest possible control, and sidesteps the sizing question rather than
answering it.

**C. Free tier, users bring their own key.** Users supply their own model credentials and pay
their own inference. The operator's cost is hosting only, and therefore flat.

## Decision

**C.**

It is the only option where the bill does not scale with adoption, which is the property that
matters for a personal project with no revenue behind it. The cost is a worse first run — a
new user cannot generate anything until they have an API key — and a credential to look after.

## What this requires

Not yet built. The shape, so the next change set is not also a design exercise:

1. **Storage.** A per-user credential — endpoint, deployment name and key — encrypted at rest
   with AES-GCM under a key from `Encryption:Key`, delivered as a platform secret. Encrypted,
   not hashed: it has to be used, not compared.
2. **Never readable back.** The API returns *whether* a key is set and its last four
   characters, never the key. A `GET` that returns it turns one XSS into a stolen credential.
3. **A runtime that uses it.** The current agent plane authenticates to Azure AI Foundry with
   the deployment's own service principal. A user-supplied key is a different path — direct
   Azure OpenAI chat completions with that key — so `IAgentRuntime` gains a second
   implementation selected per request rather than per process.
4. **Deletion.** The credential is personal data. It goes in `GET /api/me/data` as present or
   absent, and `DELETE /api/me/data` removes it.
5. **Honest failure.** A user's key can be revoked, out of quota or pointed at a deleted
   deployment. Those are the user's problem to fix, so the failure has to say which one it is
   rather than reporting "tailoring failed".

## Consequences

- **The untailored path stays free and needs no key.** Building a CV from entered facts is
  local work — bind and typeset — with no model involved. Only tailoring to a posting needs a
  credential, which keeps the product usable on first run.
- **Quotas stop being about cost and become about capacity.** `QuotaService` still bounds
  render and storage load, but the reason changes, and its numbers should be re-derived from
  the renderer's throughput rather than an inference budget.
- **The privacy policy needs a line for it** once it exists: a stored third-party credential
  is not the same category as a CV, and users should be told it is held and how.
