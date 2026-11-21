# Lead — History

## Core Context

- **Role:** Lead / Architect
- **Joined:** 2026-11-21

## Learnings

### 2026-11-21 — Onboarding

#### Architecture

One .NET 10 web project, `src/AutomaticEnvelopes.Api`, plus `tests/AutomaticEnvelopes.Tests`. Solution file is `AutomaticEnvelopes.slnx`, not a `.sln`. Docs are `README.md` and `docs/onboarding-tenant.md` only.

Two hosts, one assembly. `Program.cs` is the HTTP Lambda: `AddAwsSecureConfiguration`, `AddAuthentication`, `AddDatabase`, `AddAi`, `AddFeatures(..., validateVerifyToken: true)`, `AddWolverine`, `AddWolverineHttp`, `AddAWSLambdaHosting(HttpApi)`, then `MapWolverineEndpoints` and `RunJasperFxCommands`. `SqsLambdaHandler` is the worker: same database, AI, features, and Wolverine, but `AddFeatures` keeps the default `validateVerifyToken: false`, and it returns `SQSBatchResponse`. Do not merge those hosts.

Request path, from the webhook slice. `WhatsAppWebhookEndpoint` (`[WolverinePost("/api/whatsapp/webhook")]`) checks the signature, extracts a message, and `PublishAsync`s it. `Config.AddWolverine` sends `ProcessWhatsAppMessage` to SQS `automatic-envelopes-messages-queue` and `ChatWindowExpired` to `automatic-envelopes-system-queue`. `ProcessWhatsAppMessageHandler.Handle` loads `TenantProfile` by `BotPhoneNumberId` on a query session, then opens `LightweightSession(tenant.Id)`. Chat calls Bedrock. Dispatch lives in `Features/WhatsAppDispatcher`. Trade-off: the HTTP Lambda stays thin and the worker owns the reply. Owner of a pipeline change: Backend.

Marten in `Config.AddDatabase`: `StreamIdentity.AsString`, `TenancyStyle.Conjoined`, inline `ProcessedMessageProjection`, `HnswIndexCustomizer`. `TenantProfile` is `SingleTenanted`. `ProcessedMessage`, `DocumentChunk`, `WhatsAppRateLimitTracker`, `TenantMonthUsage`, `KnowledgeFile`, `AnonymizedChat`, and `UnansweredQuestion` are `MultiTenanted`.

Schema ownership is not the Lambda. `DatabaseSchemaStartup.AppliesAtStartup` is false when `AWS_LAMBDA_FUNCTION_NAME` is set, and then both Marten and Critter Stack use `AutoCreate.None`. Patches are SQL in `db/migrations`, applied by the Database workflow and again before API apply. Trade-off: deploys cannot drift the schema; a missed patch fails closed. Owner of a new patch: Backend. Owner of the workflow that applies it: Infra, and only by editing Terraform or the existing migrate script path, with apply left in CI.

Model calls are Bedrock, not a swappable provider. `ChatService` takes `IAmazonBedrockRuntime` and calls `ConverseAsync` under `BedrockSettings`. `Microsoft.Extensions.AI.ChatMessage` is the history type. `EmbeddingService` calls `InvokeModelAsync` with `TitanEmbeddingsModelId` `amazon.titan-embed-text-v2:0` at 512 dimensions. README still lists Ollama, OpenAI, and Gemini. Do not add that second client. Owner of a model change: Backend.

What not to build: a second project or mediator, a second Meta app or per-tenant verify token, schema apply on either Lambda, another LLM SDK, and the PDF-extraction queue README already defers.

Infra roots seen under `infra/`: `bootstrap`, `network`, `db`, `serverless`, `portal`, `api-dns`, `api-delegation`, `modules/api-edge`. Apply stays on the reusable workflows (`plan` / `approve` / `apply` in `reusable-terraform.yml`, `reusable-serverless.yml`, `reusable-db-migrate.yml`, `reusable-portal.yml`, `reusable-api-domain.yml`). `db.yml` and `deploy.yml` call those. `destroy.yml` is `workflow_dispatch` with jobs `confirm`, `approve`, `destroy`. I did not run Terraform or the AWS CLI.

#### Conventions

Slice shape, patch naming, the shared test host, the two Lambdas, conjoined tenancy, Bedrock-only model calls, and the single Meta app are team rules. They are in `.squad/decisions/inbox/lead-onboarding-conventions.md`. They do not repeat the seven standing decisions.

Decision 5's names match the workflows. There is no job id `build`. `pr-check.yml` job `build-test` (display name "Build and test") only runs `docker build`, and its path filter is `src/**` and `Dockerfile`. `deploy.yml` job `build-and-test` runs `dotnet build` and `dotnet test` on the API and test projects. `sonarcloud.yml` job `sonarcloud` has a step named `Build` that builds `AutomaticEnvelopes.slnx`. The included list is right, so no correction was filed.

Bot-facing copy already in the product stays in that language. `UserMessages.Unavailable` is Spanish. GitHub text stays English. That is standing decision 4, not a new rule.

Config is named, not valued: `ConnectionStrings:Marten`, `SECRET_ARN_MARTEN`, `BedrockSettings`, `WhatsApp:VerifyToken`, `Usage:PhoneHashKey`. `appsettings.json` has no Marten connection string. That is standing decision 1.

#### Key files

- `README.md`, `docs/onboarding-tenant.md`
- `AutomaticEnvelopes.slnx`
- `src/AutomaticEnvelopes.Api/AutomaticEnvelopes.Api.csproj` (net10.0; Marten 9.51.0; WolverineFx 6.51.2; WolverineFx.Http; WolverineFx.Marten; WolverineFx.AmazonSqs; Pgvector)
- `src/AutomaticEnvelopes.Api/Program.cs`, `SqsLambdaHandler.cs`, `Config.cs`, `DatabaseSchemaStartup.cs`
- `src/AutomaticEnvelopes.Api/Features/WhatsAppWebhook/WhatsAppWebhookEndpoint.cs`, `ProcessWhatsAppMessageHandler.cs`
- `src/AutomaticEnvelopes.Api/Features/Chat/Config.cs`, `ChatService.cs`, `UserMessages.cs`
- `src/AutomaticEnvelopes.Api/Features/Knowledge/Services/EmbeddingService.cs`
- `src/AutomaticEnvelopes.Api/Features/Tenancy/TenantAccess.cs` (`TenantIds`, `TenantAccess`)
- `src/AutomaticEnvelopes.Api/appsettings.json`
- `db/migrations/20261026_001_esquema_inicial_marten_9_51_wolverine_6_51.sql`, `db/migrations/20261028_001_chat_debounce_window_claim.sql`
- `tests/AutomaticEnvelopes.Tests/AutomaticEnvelopes.Tests.csproj`, `IntegrationAppFixture.cs`
- `.github/workflows/pr-check.yml`, `deploy.yml`, `db.yml`, `sonarcloud.yml`, `reusable-terraform.yml`, `reusable-serverless.yml`, `reusable-db-migrate.yml`, `reusable-api-domain.yml`
- `.squad/routing.md`, `.squad/decisions.md`

Feature folders opened under `src/AutomaticEnvelopes.Api/Features`: `AdminAuth`, `Chat`, `Knowledge`, `Portal`, `Tenancy`, `Usage`, `WhatsAppDispatcher`, `WhatsAppWebhook`. HTTP routes use `[WolverineGet]`, `[WolverinePost]`, `[WolverinePatch]`, and `[WolverineDelete]` on endpoint types in those folders. `Config.AddFeatures` registers WhatsApp options, chat, webhook, knowledge, and the dispatcher. Portal, tenancy, usage, and admin auth are picked up by Wolverine assembly discovery (`Discovery.IncludeAssembly(typeof(Program).Assembly)`). `UsageMeter` is registered from `AddAi`.

#### Gotchas

`db-patch` is not a type in this repo. `Program.cs` ends in `RunJasperFxCommands`, and the 20261028 migration header records `dotnet run --project src/AutomaticEnvelopes.Api -- db-patch db/migrations/YYYYMMDD_NNN_descripcion.sql` against a database that already has the previous patch. The four files are `20261026_001`, `20261026_002`, `20261026_003`, and `20261028_001`. The first three slugs are Spanish; the last is English. Lexical order is the contract. The 20261028 patch grants `SELECT, INSERT, UPDATE, DELETE` on the new table to `marten_app` and does not make that role the owner.

`pr-check.yml` does not run `dotnet test`. A pull request that only touches `tests/**` misses that workflow. SonarCloud and the API deploy workflow are what run the suite. Production codegen is `TypeLoadMode.Static`; SonarCloud's step "Pre-generate JasperFx static code" runs `codegen write` before `Build`.

`AutomaticEnvelopes.slnx` still lists older infra paths such as `infra/db/main.tf` and omits files the tree now has (`infra/serverless/ses.tf`, `api_edge.tf`, `logs.tf`). It is the SonarCloud build input, not the infra inventory.

`IntegrationAppFixture` sets `ConnectionStrings__Marten` from Testcontainers `pgvector/pgvector:pg16`, starts ElasticMQ, and forces `AutoCreate.All` for the Alba host in environment `Testing`. That auto-create is the test host only. The collection name is `Integration` (`IntegrationCollection`).

Routing rule 8: this Cursor host has no background runs. Scribe cannot be fire-and-forget. Squad state tools were not in this session; `.squad/` files are the backend. No `upstream.json`.
