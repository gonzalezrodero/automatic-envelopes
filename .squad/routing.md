# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, scope, and ADRs | Lead | What to build, what to leave out, cross-cutting design |
| .NET 10 API, Wolverine, Marten, pgvector, Bedrock, Lambda image code | Backend | `src/AutomaticEnvelopes.Api`, vertical slices |
| Marten SQL patches | Backend | `db/migrations/YYYYMMDD_NNN_*.sql`, reviewed before commit |
| xUnit, Alba, Testcontainers | Tester | `tests/AutomaticEnvelopes.Tests` |
| Terraform | Infra | `infra/**`. Diffs only; apply stays on the existing Terraform workflows |
| Review of any code or test change | Reviewer | Pull request review. The reviewer is never the author |
| Docs for GitHub | Lead | `README.md`, `docs/`. English, no secrets |

Preset installation adds concrete routes for the configured team. Add or edit rows
here only when their agent names also exist in the casting registry.

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. **Eager by default** — spawn all agents who could usefully start work, including anticipatory downstream work.
2. **Scribe always runs** after substantial work, always as `mode: "background"`. Never blocks.
3. **Quick facts → coordinator answers directly.** Don't spawn an agent for "what port does the server run on?"
4. **When two agents could handle it**, pick the one whose domain is the primary concern.
5. **"Team, ..." → fan-out.** Spawn all relevant agents in parallel as `mode: "background"`.
6. **Anticipate downstream work.** If a feature is being built, spawn the tester to write test cases from requirements simultaneously.
7. **Issue-labeled work** — when a `squad:{member}` label is applied to an issue, route to that member. The Lead handles all `squad` (base label) triage.
8. **Cursor has no background runs.** This host does not declare `background-runs`. Do not rely on fire-and-forget Scribe. When a decision must land in `decisions.md` before the session ends, run Scribe in the same turn and wait.
9. **One reviewer who did not author the change.** Backend, Infra, and Tester implement. Reviewer reviews. Lead does not implement the change they are scoping.
10. **Standing rules in `decisions.md` win** over a generic charter when they conflict: no secrets or tenant data in any file, no local Terraform apply or AWS CLI changes, minimal code and tests, English on GitHub, and the CI job name `build` stays as it is.

## Work Type → Agent

| Work Type | Primary | Secondary |
|-----------|---------|----------|
| Lead / Architect | Lead | Reviewer |
| Code Reviewer | Reviewer | — |
| Backend Developer | Backend | Tester |
| DevOps Engineer | Infra | Reviewer |
| Test Engineer | Tester | Reviewer |
