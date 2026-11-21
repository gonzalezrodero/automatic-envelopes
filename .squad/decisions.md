# Squad Decisions

## Active Decisions

### 2026-11-21: Public repository — no secrets or customer data

**By:** Lead
**What:** Never write secrets, tokens, API keys, WhatsApp or Meta values, customer data, or tenant data into `.squad/` or any other file. This repository is public. Describe configuration by name (`SECRET_ARN_MARTEN`, `ConnectionStrings:Marten`) and keep values in AWS Secrets Manager, SSM, or local untracked files such as `appsettings.Development.json`.
**Why:** A committed secret or tenant record cannot be unpublished. Squad state, charters, history, decisions, and logs are committed with the code.

### 2026-11-21: Infrastructure changes go through Terraform CI only

**By:** Lead
**What:** Infrastructure changes are pull requests to Terraform under `infra/`. Apply, plan, and approval stay on the existing Terraform GitHub Actions workflows. Never run `terraform apply`, `terraform destroy`, or the AWS CLI to create, change, or delete resources from a workstation or an agent session. Do not edit `.github/workflows/` to bypass that pipeline.
**Why:** The account, state backends, and approval gates already live in CI. A local apply races that pipeline and can leak credentials into agent transcripts.

### 2026-11-21: Minimal code and tests

**By:** Lead
**What:** Keep code and tests minimal. Change only what the task needs. Prefer the existing vertical slice, Marten document, Wolverine handler, and xUnit/Alba/Testcontainers patterns over new projects, frameworks, or abstractions.
**Why:** The codebase is a small .NET 10 service. Extra surface area is harder to review and to run in CI.

### 2026-11-21: GitHub is in English

**By:** Lead
**What:** Issues, pull requests, commit messages, code comments, and docs written for GitHub are in English.
**Why:** Reviewers and CI logs are shared in English. Product copy that the bot sends to end users can stay in the language that feature already uses.

### 2026-11-21: Do not rename the CI job `build`

**By:** Lead
**What:** The CI job name `build` must not change. Do not edit workflow files to rename jobs or steps. Jobs already in this repo include `build-test` (pull request), `build-and-test` (deploy), and `sonarcloud`, plus a step named Build.
**Why:** Required checks and the deploy chain key off those names. Renaming a job makes a green change look red, or skips a gate.

### 2026-11-21: Squad product skills are not this repo's conventions

**By:** Lead
**What:** `.github/skills/squad-conventions` and the Squad CLI skills under `.squad/templates/skills/` (`release-process`, `versioning-policy`, `cli-wiring`, `ci-validation-gates`, and anything that says `node:test` or `package.json`) describe the Squad product. They do not apply to Automatic Envelopes. This repo stays .NET 10, xUnit, Alba, Testcontainers, and Terraform. Do not add an npm dependency to the project.
**Why:** `squad init` copies those skills into every repo. Following them here would invent a Node test runner.

### 2026-11-21: Economy model profile on Cursor

**By:** Lead
**What:** `.squad/config.json` sets `modelProfile` to `economy` and `economyMode` to true. Charters leave `Preferred: auto` so this profile is not overridden. On Cursor the session model still wins when the host cannot pass a per-spawn model; pick an inexpensive model in the Cursor model picker.
**Why:** Squad work here runs through Cursor. The economy profile is the cheap path Squad documents (`gpt-5.6-luna` after the economy map on hosts that honor it).

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
- Standing rules above override a specialist's generic charter when they conflict
