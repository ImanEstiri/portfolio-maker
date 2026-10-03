# ADR 0004 — Quota is charged on acceptance; failed generations are not refunded

**Status:** Accepted
**Date:** 2026-10-03

## Context

Tailoring a CV to a posting costs model tokens, so tailored generations are metered. The unit is
consumed when the request is accepted — before any model call, the only point that gates spend —
and the job can still fail afterwards: the model run times out or breaks the schema, the fact gate
rejects its output, the renderer or the blob write fails. The user has then spent a unit of a
small allowance and received nothing. Does a failure give it back?

## Options

**A. Refund every failure.** Fairest to the user, and an abuse vector: input crafted to fail
reliably after the agent calls — a posting that steers the model into breaking the schema, say —
yields free inference, because the tokens are spent either way and only the counter rolls back.

**B. Refund by cause.** Give the unit back when the service caused the failure (a timeout, a
crash, a failed write); keep it when the input drove it (a schema violation, a fact-gate
rejection). Right in principle, but it needs a refund operation and a trustworthy cause.

**C. Never refund, and record the cause.** Nothing to abuse. Users absorb infrastructure
failures, and the data needed to judge B accumulates in the meantime.

## Decision

**C.** A refund path built before anyone knows how often it matters is an abuse surface added to
fix an unmeasured problem. Every failure carries its cause, so B can follow from evidence.

## What exists today

- **The meter.** `QuotaService.TryConsumeAsync` is one conditional `UPDATE … WHERE used < limit`,
  so concurrent requests cannot share a unit. `POST /api/documents` calls it only when the request
  has a posting, before any row is written; an empty allowance is a `402`. The period rolls 30
  days from first use and holds 3 units by default (`Quota:Free`). Every account is `Free`:
  `QuotaTier.Pro` and `Quota:Pro` exist and nothing assigns them.
- **The cause.** `GenerationJobs.FailureCause` is set at every failure site and returned on the
  job. Ours: `RenderUnavailable`, `RenderTimeout`, `RenderCrashed`, `StorageFailure`,
  `WorkerRestart` (cleared when the recovered job runs again), `AgentUnavailable`,
  `AgentTimeout`. Driven by the input: `TemplateNotFound`, `BindingFailed`,
  `FactValidationFailed`, `SchemaValidationFailed`, `PageBudgetExceeded` (never set).
  `Cancelled` is neither.
- **Deferred: the refund.** Nothing decrements a counter, and cancelling a queued job does not
  return its unit. B needs a refund operation on `QuotaService` and a rule from the groups above.
- **Deferred: the measurement.** A tailored job consumed a unit exactly when its document has a
  `PostingId`, so "consumed but failed" is a query; nothing runs or logs it as such.
- **Gaps in the cause.** A schema or fact-gate failure counts as input-driven even when the model
  was at fault, deliberately: a user who can steer the model through a posting can cause both on
  purpose. A request that fails after the unit is consumed but before the job is saved — a posting
  longer than its 60,000-character column, for instance — has no job to carry a cause.

## Consequences

- **A failed tailoring costs the user a unit.** The UI calls the allowance "tailored
  generations" but does not say that failures count.
- **Bring-your-own-key changes the question.** If ADR 0003 is built, tailoring spend moves to
  the user's own key and the unit bounds capacity, not cost, weakening the case for a refund.
