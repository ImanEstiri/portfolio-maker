## What this changes

<!-- One or two sentences. What behaviour is different after this PR? -->

## Why

<!-- Link the issue if there is one. If not, describe the problem this solves. -->

Closes #

## Approach and alternatives

<!-- What you did, and what you considered and rejected. -->

## Security impact

<!--
Anything touching the LaTeX escaper, the renderer sandbox, token validation, CORS or
ownership checks. "None — documentation only" is a fine answer.
-->

## Schema changes

<!--
Changed the model? There must be a matching migration — EF Core refuses to migrate a
model with pending changes, so without one the API will not start. "None" if none.
-->

## Checklist

- [ ] `dotnet build CvMaker.sln` passes
- [ ] `dotnet test CvMaker.sln` passes
- [ ] Behaviour changes are covered by a test
- [ ] Escaper or sandbox changes have a case in the injection corpus
- [ ] Docs updated where the change is user-visible
