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

### 2026-11-21: Copilot projection and Squad templates are not committed

**By:** Lead
**What:** The Copilot files from `squad init` (`.github/agents/`, `.github/skills/`, `.mcp.json`, `.copilot/`) and `.squad/templates/` were removed on purpose. Cursor loads `.cursor/agents/squad.md`, `.cursor/rules/squad.mdc`, and `.cursor/mcp.json`. Team state stays in `.squad/` (roster, charters, routing, decisions). Do not add an npm dependency, and do not restore the Squad CLI skill pack or its workflow templates.
**Why:** Those files are the Copilot host and the Squad product's own scaffolding. The Cursor coordinator file already contains the protocol. `squad doctor` still looks for `.github/agents/squad.agent.md` and root `.mcp.json`; that check is the Copilot layout, and this repo leaves it unmet.

### 2026-11-21: Default model profile on Cursor

**By:** Lead
**What:** `.squad/config.json` sets no `modelProfile`, so Squad uses its default `quality-first` profile. Charters leave `Preferred: auto`. On Cursor the session model still wins when the host cannot pass a per-spawn model.
**Why:** Squad's author says the economy profile ends up costing more through retries and weaker work (PR #138).

### 2026-11-21: Onboarding conventions

#### 2026-11-21: One project, Wolverine vertical slices

**By:** Lead
**What:** Product code stays in `src/AutomaticEnvelopes.Api`. A feature is a folder under `Features/` with a Wolverine endpoint (`[WolverineGet]`, `[WolverinePost]`, `[WolverinePatch]`, or `[WolverineDelete]`) and/or a `Handle` method, plus an `AddXFeature` extension only when it needs DI. Register that extension from `Config.AddFeatures`. Wolverine already scans `typeof(Program).Assembly`. Do not add a second project, a controller layer, or another mediator.
**Why:** `AutomaticEnvelopes.slnx` has one product project and one test project. `Config.AddFeatures` composes the feature extensions. `WhatsAppWebhookEndpoint` and `ProcessWhatsAppMessageHandler` are the slice shape. Portal, tenancy, and admin routes are the same attributes on endpoint types under `Features/`.

#### 2026-11-21: Marten patches are dated SQL, not Lambda startup

**By:** Lead
**What:** Schema changes are a new file `db/migrations/YYYYMMDD_NNN_snake_case.sql`. `NNN` orders same-day files so a lexical sort is chronological. Generate it with `dotnet run --project src/AutomaticEnvelopes.Api -- db-patch <that path>` against a database that already has the previous patch, then review the SQL. Do not let either Lambda apply Marten schema. `DatabaseSchemaStartup` is false when `AWS_LAMBDA_FUNCTION_NAME` is set, and `Config.AddDatabase` then sets `AutoCreate.None`.
**Why:** `README.md` (Local database) and the header of `db/migrations/20261028_001_chat_debounce_window_claim.sql`. `Program.cs` returns `RunJasperFxCommands`. `DatabaseSchemaStartup.cs` and `Config.AddDatabase`. Existing files: `20261026_001`, `20261026_002`, `20261026_003`, `20261028_001`.

#### 2026-11-21: Integration tests share one Alba host

**By:** Lead
**What:** A test that needs the HTTP pipeline or Postgres uses `[Collection("Integration")]` and `IntegrationAppFixture`. That fixture is `AlbaHost.For<Program>` in environment `Testing`, Postgres `pgvector/pgvector:pg16` via Testcontainers, ElasticMQ for the two app queues, and `ConnectionStrings__Marten` set to the container. Bedrock, WhatsApp, and SES are mocks. `AutoCreate.All` belongs in that fixture only. Do not point tests at RDS, and do not add a second host bootstrap.
**Why:** `tests/AutomaticEnvelopes.Tests/IntegrationAppFixture.cs` (`IntegrationCollection`) and `AutomaticEnvelopes.Tests.csproj` (xunit.v3, Alba, Testcontainers.PostgreSql).

#### 2026-11-21: HTTP Lambda and SQS worker stay separate hosts

**By:** Lead
**What:** `Program.cs` is the only host that validates the WhatsApp verify token (`AddFeatures(..., validateVerifyToken: true)`), maps Wolverine HTTP endpoints, and uses `AddAWSLambdaHosting`. `SqsLambdaHandler` is the worker and calls `AddFeatures` without that flag. Do not copy the API middleware, the verify-token gate, or schema apply onto the worker, and do not move the SQS batch loop into `Program.cs`.
**Why:** `src/AutomaticEnvelopes.Api/Program.cs` and `src/AutomaticEnvelopes.Api/SqsLambdaHandler.cs`. `Config.AddFeatures` defaults `validateVerifyToken` to false.

#### 2026-11-21: Conjoined tenancy and the tenant id rule

**By:** Lead
**What:** Marten tenancy stays `TenancyStyle.Conjoined`. `TenantProfile` stays `SingleTenanted`. The other documents registered in `AddDatabase` stay `MultiTenanted`. Tenant-scoped reads and writes use `LightweightSession(tenant.Id)` after the profile is resolved. A tenant id is `TenantIds.IsValid`: `^[a-z0-9-]+$`, 1 to 64 characters, and not `admin`. Do not add a row-level scheme, a database per tenant, or a tenant id that fails that rule.
**Why:** `src/AutomaticEnvelopes.Api/Config.cs` (`AddDatabase`) and `src/AutomaticEnvelopes.Api/Features/Tenancy/TenantAccess.cs`. `ProcessWhatsAppMessageHandler` opens the session with the resolved tenant id.

#### 2026-11-21: Bedrock is the only model client

**By:** Lead
**What:** Chat goes through `IAmazonBedrockRuntime.ConverseAsync` and `BedrockSettings`. Embeddings go through `EmbeddingService.InvokeModelAsync` and the existing Titan model id at 512 dimensions. `Microsoft.Extensions.AI` is the chat-history type, not a second provider. Do not add an Ollama, OpenAI, or Gemini client. A tool the bot can call has to be on the API allowlist in the same slice; a name that is not on that list is rejected, and an empty list attaches nothing.
**Why:** `src/AutomaticEnvelopes.Api/Features/Chat/ChatService.cs`, `src/AutomaticEnvelopes.Api/Features/Knowledge/Services/EmbeddingService.cs`, `Config.AddAi`, and `src/AutomaticEnvelopes.Api/appsettings.json` (`BedrockSettings`). README's provider list is ahead of the code.

#### 2026-11-21: One Meta app; route on BotPhoneNumberId

**By:** Lead
**What:** All tenants share one Meta app, one `WhatsApp:AppSecret`, and one `WhatsApp:VerifyToken`. Each tenant has its own access token at `/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token` and its own `TenantProfile.BotPhoneNumberId`. The worker finds the tenant by that phone-number id. Do not add a second Meta app, a per-tenant verify token, or a route that uses the display phone.
**Why:** `docs/onboarding-tenant.md` and `ProcessWhatsAppMessageHandler.GetTenantAsync`, which queries `TenantProfile.BotPhoneNumberId`.

#### 2026-11-21: Production JasperFx codegen stays static

**By:** Lead
**What:** Production `GeneratedCodeMode` stays `TypeLoadMode.Static`. New handlers and endpoints must still compile under the existing `codegen write` step. Do not switch production to runtime generation, and do not add a code-generation package or project to replace it.
**Why:** `Config.AddDatabase` (`CritterStackDefaults`) and `.github/workflows/sonarcloud.yml` step "Pre-generate JasperFx static code", which runs `dotnet run --project src/AutomaticEnvelopes.Api/AutomaticEnvelopes.Api.csproj -- codegen write` before the step named Build.

### 2026-11-21: Global unique index on TenantProfile.BotPhoneNumberId

**By:** Lead
**What:** `TenantProfile.BotPhoneNumberId` has a global computed Marten unique index (`mt_doc_tenantprofile_uidx_bot_phone_number_id`), shipped as dated migration `20261121_001`. It is global because `TenantProfile` is single-tenanted and the table has no `tenant_id`. `RegisterTenantEndpoint` keeps its 400 check; the index is the race backstop.
**Why:** Two tenants must never share a WhatsApp bot number, since inbound routing looks the tenant up by that number.
