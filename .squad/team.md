# Squad Team

> automatic-envelopes

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Lead | Lead / Architect | `.squad/agents/lead/charter.md` | ✅ Active |
| Reviewer | Code Reviewer | `.squad/agents/reviewer/charter.md` | ✅ Active |
| Backend | Backend Developer | `.squad/agents/backend/charter.md` | ✅ Active |
| Infra | DevOps Engineer | `.squad/agents/infra/charter.md` | ✅ Active |
| Tester | Test Engineer | `.squad/agents/tester/charter.md` | ✅ Active |

## Built-in Support Agents

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Scribe | Decision Merger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | `.squad/agents/ralph/charter.md` | 🔄 Monitor |
| Rai | RAI Reviewer | `.squad/agents/rai/charter.md` | 🛡️ RAI |
| Fact Checker | Devil's Advocate & Verification Agent | `.squad/agents/fact-checker/charter.md` | 🔍 Verifier |

## Project Context

- **Project:** automatic-envelopes
- **Created:** 2026-11-21
- **Stack:** .NET 10 API (`src/AutomaticEnvelopes.Api`): Marten on PostgreSQL with pgvector, Wolverine, Amazon Bedrock, Lambda container images. Tests: xUnit, Alba, Testcontainers (`tests/AutomaticEnvelopes.Tests`). Schema patches: `db/migrations`. Terraform: `infra/`, applied only by the existing GitHub Actions workflows.
- **Team:** five specialists (Lead, Reviewer, Backend, Infra, Tester). Scribe, Ralph, Rai, and Fact Checker are built-in support agents, not extra specialists.
- **Host:** Cursor. Model profile: `economy` (see `.squad/decisions.md`).
- **Language:** English on GitHub. The repository is public.
