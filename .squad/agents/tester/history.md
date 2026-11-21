# Tester — History

## Core Context

- **Role:** Test Engineer
- **Joined:** 2026-11-21

## Learnings

### 2026-11-21 — Onboarding

#### Architecture

`tests/AutomaticEnvelopes.Tests` is the only test project. It targets `net10.0`, references `src/AutomaticEnvelopes.Api`, and uses xUnit v3 (`xunit.v3` 4.0.2, `xunit.runner.visualstudio` 4.0.1, `Microsoft.NET.Test.Sdk` 18.11.0). `global.json` sets `test.runner` to Microsoft.Testing.Platform. Assertions are AwesomeAssertions 9.6.0. There is no `xunit.runner.json`, no `[assembly: CollectionBehavior]`, and no custom `WebApplicationFactory`. `Microsoft.AspNetCore.Mvc.Testing` is referenced and unused.

Two speeds:

- **Integration** is one xUnit collection, `"Integration"`. `IntegrationCollection` is the `ICollectionFixture<IntegrationAppFixture>`. The fixture implements `IAsyncLifetime` and starts a single Testcontainers PostgreSQL container (`pgvector/pgvector:pg16`, database `automatic_envelopes_test`) plus an ElasticMQ container (`softwaremill/elasticmq-native`) on a random host port for 9324. After a fixed 2-second wait it creates `automatic-envelopes-messages-queue`, `automatic-envelopes-system-queue` (`DelaySeconds` 10), and `wolverine-dead-letter-queue`. It then sets process environment variables and boots **one** `AlbaHost.For<Program>` for the whole collection. xUnit runs that collection serially. Classes outside it run in parallel with it and with each other.
- **Narrow** tests do not join the collection, so they do not start PostgreSQL. They call the type directly, or build it with Moq.AutoMock, and pass a mocked `IDocumentSession` / `IDocumentStore` when a collaborator is Marten. That is for rules, mapping, and HTTP-result branching. Anything that reads or writes Marten, including pgvector, belongs on the shared container.

The host is the real `Program`: environment `"Testing"`, `ValidateScopes = false`, Alba `AuthenticationStub` named `IntegrationTestUser` with role `admin`. `Program` reads `GetConnectionString("Marten")` while the host is built, so the fixture assigns `ConnectionStrings__Marten` from `_postgres.GetConnectionString()` **before** `AlbaHost.For`. It also sets `EnableSqsListener=true`, `BedrockSettings__ModelId`, `Usage__PhoneHashKey`, `Usage__FromAddress`, `AWS_REGION=eu-west-1`, placeholder AWS credentials, and `AWS_ENDPOINT_URL` / `AWS_ENDPOINT_URL_SQS` at the ElasticMQ URL. Inside `ConfigureServices` it replaces `IAmazonBedrockRuntime`, `IWhatsAppClient`, and `IAmazonSimpleEmailServiceV2` with mocks, forces JasperFx `TypeLoadMode.Auto` (production static codegen is not required to boot tests), and sets Marten `AutoCreate.All` plus the same tenanted document map as `AddDatabase`. `appsettings.Testing.json` only lists admin CORS origins. Deployed Lambdas skip schema changes when `AWS_LAMBDA_FUNCTION_NAME` is set (`DatabaseSchemaStartup`); the test host leaves that variable empty, so startup applies Marten changes and calls `VectorSchema.Ensure` on the same connection string. pgvector tests do not start a second container. `VectorSchemaPrivilegeTests` and `DeployedDatabaseSchemaTests` open Npgsql with `ConnectionStrings__Marten` and create a scratch database or a non-superuser role on that server.

Wolverine is the in-process bus on that host (`DurabilityMode.Solo`, SQS transport pointed at ElasticMQ, listeners on because `EnableSqsListener` is true). Handler tests do not sleep for the 10-second system queue. They call `IAlbaHost.InvokeMessageAndWaitAsync` (`Extensions/WolverineTestExtensions.cs`): `ExecuteAndWaitAsync` plus `IMessageBus.InvokeAsync`, which waits for cascading handlers. HTTP tests use `fixture.Host.Scenario`. `SeedTenantAsync` stores a `TenantProfile` through `IDocumentStore.LightweightSession()`.

#### Conventions

One behavior, one `[Fact]` or a small `[Theory]`. Integration classes take `IntegrationAppFixture` in the primary constructor and carry `[Collection("Integration")]`. Copy the nearest class: Alba `Scenario` for an endpoint, `InvokeMessageAndWaitAsync` for a handler, mocked session or AutoMocker when the slice never touches the database.

Per feature, fixture and pattern:

- **Chat.** Integration: `ChatSessionHandlerTests`, `ChatDebounceSagaIntegrationTests` — seed or append a stream, invoke the command or `ChatWindowExpired`, read the stream with `LightweightSession(tenant)`. Narrow: `ChatServiceTests` and tool tests via AutoMocker; `ChatDebounceSagaTests` calls the saga method with a mocked session; `TenantIsolationTests`, unanswered inbox, prompt, and personal-data tests call the handler or pure function with fakes (`tenant-a`, `+15550000000`).
- **Knowledge.** Integration: `KnowledgeReplaceIntegrationTests` (real chunks on the shared store), `E2E/IngestEndpointTests` (Alba multipart, then query chunks), `VectorSchemaPrivilegeTests` (Npgsql on the fixture connection). Narrow: `Features/Knowledge/IngestEndpointTests` and `KnowledgeIngestionServiceTests` / `KnowledgeBaseServiceTests` via AutoMocker and a mocked session; `VectorSchemaSqlTests` asserts the SQL string only; PDF/text extractors are file-bytes in memory.
- **Tenancy.** Integration: `RegisterTenantEndpointTests`, `TenantEndpointsIntegrationTests` — `Scenario` against `/api/admin/tenants` and `/tenants`, then `QuerySession` / `LoadAsync`. Narrow: `TenantEndpointsTests` calls the endpoint methods with a strict `Mock<IDocumentSession>` (401 does not query); `TenantAccessTests` and `TenantPortalProfileTests` are pure.
- **WhatsApp.** Integration: `ProcessWhatsAppMessageHandlerTests` invokes `ProcessWhatsAppMessage` and reads the tenant stream; `ProcessedMessageProjectionTests` appends and loads the inline projection. Narrow: `WhatsAppWebhookEndpointTests` calls `VerifyChallenge`; `WhatsAppPayloadProcessorTests` is AutoMocker; dispatcher tests mock `IWhatsAppClient`, a token source, or an `HttpMessageHandler`. Do not put a live token or a real webhook body in the test.
- **Portal.** Integration: `PortalEndpointsIntegrationTests` — `Scenario` plus `AsTenant`, which removes the admin role and `cognito:groups` claims and sets role to the tenant id, then checks 200 vs 403. Narrow: `PortalScreenTests` and `UnansweredEndpointTests` call the screen or endpoint with mocks.
- **Usage.** Integration: `MonthlyUsageIntegrationTests` — seed usage, `InvokeMessageAndWaitAsync` for `AnalyzeChatSession`, drain `SentEmails`, then GET the usage route with `Scenario`. Narrow: `UsageRulesTests` checks month keys and phone hashing with an obvious fake key.
- **Auth.** Integration: `AdminCsrfIntegrationTests` — cookie header plus `Origin` (`http://localhost:5173` is allowed; anything else is 403) on ingest and tenant patch. Narrow: `AdminCsrfMiddlewareTests` drives the middleware on a fake `HttpContext`; `AuthEndpointsTests` and Cognito tests use mocked token clients or stub `HttpMessageHandler`s; config and rate-limit key tests do not boot the host.

Assertions are `value.Should()...`. Async integration tests pass `TestContext.Current.CancellationToken` into Marten. New tenants and phones are `$"tenant-a-{Guid.NewGuid():N}"` and `+15550000000` (or a random `+1555…` suffix). The default Alba user is already an admin; a tenant-scoped route must switch claims the way `AsTenant` does. Reset shared fixture queues (`BedrockClientMock.Invocations.Clear()`, drain `SentEmails`) at the start of a test that counts calls or mail, and clear `BeforeConverse` in a `finally`.

#### Key files

- `tests/AutomaticEnvelopes.Tests/AutomaticEnvelopes.Tests.csproj` — packages and `net10.0`.
- `tests/AutomaticEnvelopes.Tests/IntegrationAppFixture.cs` — container lifecycle, env injection, Alba host, mocks, `SeedTenantAsync`, `[CollectionDefinition("Integration")]`.
- `tests/AutomaticEnvelopes.Tests/Extensions/WolverineTestExtensions.cs` — `InvokeMessageAndWaitAsync`.
- `src/AutomaticEnvelopes.Api/Program.cs` — reads the Marten connection string, applies schema unless `codegen` or a Lambda name is set, ensures pgvector, maps Wolverine HTTP.
- `src/AutomaticEnvelopes.Api/Config.cs` — `AddDatabase`, Wolverine SQS listeners behind `EnableSqsListener`.
- `src/AutomaticEnvelopes.Api/DatabaseSchemaStartup.cs` — empty `AWS_LAMBDA_FUNCTION_NAME` means apply schema.
- `src/AutomaticEnvelopes.Api/appsettings.Testing.json` — portal origins for the Alba host.
- `tests/AutomaticEnvelopes.Tests/E2E/FullPipelineE2ETests.cs` — same collection, not a second host; polls with `WaitForConditionAsync`.
- `tests/AutomaticEnvelopes.Tests/DeployedDatabaseSchemaTests.cs` and `Features/Knowledge/VectorSchemaTests.cs` — scratch databases on the fixture server.
- `.github/workflows/pr-check.yml` — job id `build-test`, name "Build and test". Pull requests to `main` that touch `src/**` or `Dockerfile`. Docker build only. It does not run `dotnet test`.
- `.github/workflows/deploy.yml` — job id `build-and-test`, name "Build and test". Push to `main` (paths include `tests/**`) and `workflow_dispatch`. `actions/setup-dotnet` `10.0.x`, then `dotnet test --project tests/AutomaticEnvelopes.Tests/AutomaticEnvelopes.Tests.csproj --configuration Release`. Later jobs: `migrate-dev`, `deploy-dev`, `migrate-prod`, `deploy-prod`, `deploy-api-domain`. Do not rename a CI job to `build`.
- `.github/workflows/sonarcloud.yml` — job id `sonarcloud`, name "SonarCloud". Push and pull request to `main`, no path filter. This is the PR run that executes tests: `dotnet run … -- codegen write`, `dotnet build AutomaticEnvelopes.slnx`, then `dotnet-coverage collect "dotnet test AutomaticEnvelopes.slnx --no-build"`.

#### Gotchas

- The Integration collection needs a running Docker daemon and a pull of `pgvector/pgvector:pg16` and ElasticMQ. If Docker is down, every `[Collection("Integration")]` test fails together; narrow tests still pass. Do not stub Marten to “skip” that. Do not construct a second `IntegrationAppFixture` or a second PostgreSQL container.
- Forgetting `[Collection("Integration")]` means no host and no `ConnectionStrings__Marten`. Putting a pure test in the collection still pays for container startup. `VectorSchemaSqlTests` stays out; privilege and baseline tests stay in and read the env var the fixture set.
- `ConnectionStrings__Marten`, AWS endpoint variables, and `EnableSqsListener` are process-wide. The host copies the connection string at boot; changing the variable later does not retarget Alba. `AwsConfigurationExtensionsTests` clears `SECRET_ARN_MARTEN`, `SSM_PATH_WHATSAPP`, `SECRET_ARN_USAGE`, and `DB_HOST` in its constructor and `Dispose`. Those names do not overlap the fixture’s Marten variable, but any new test that clears `ConnectionStrings__Marten` or `AWS_ENDPOINT_URL_SQS` while the collection is starting will boot the host against the wrong store or a real AWS endpoint.
- `DeployedDatabaseSchemaTests` sets `AWS_LAMBDA_FUNCTION_NAME` and must restore the previous value. Left set, `DatabaseSchemaStartup.AppliesAtStartup()` flips for every later test in the process.
- One database for the whole collection. Fixed tenant ids and phone numbers collide with earlier tests in the same run. Use a guid suffix. Do not `DROP` the fixture database. Scratch databases in the vector and baseline tests must terminate backends before `DROP DATABASE`.
- `BeforeConverse`, `ConverseTexts`, `SentEmails`, and the three mocks are shared. A test that sets `BeforeConverse` and does not clear it makes the next converse throw or hang. Count mail only after draining `SentEmails`. The Bedrock mock answers from substrings in the prompt (`998877`, the privacy-filter phrase). A new prompt that contains those substrings changes the canned reply.
- The SQS listener is on. Handler tests should `InvokeMessageAndWaitAsync`, not publish and sleep for the 10-second delayed system queue. `FullPipelineE2ETests` polls every 300ms for up to 15–25 seconds; that is the flaky shape. Prefer the tracked invoke unless the scenario is the webhook-to-queue path.
- Alba’s stub user is an admin. Portal tests that forget `AsTenant` look authorized for every tenant. CSRF integration tests must send the cookie and an allowed `Origin`; a bearer header skips the cookie check in the narrow middleware tests.
- Webhook signatures are HMAC of the exact raw body using the fake app secret the fixture already configured. Do not embed a real Meta token, a customer payload, or a live phone. Temp PDFs in the E2E ingest path go under the system temp directory and must be deleted, as `FullPipelineE2ETests` does.
- Two classes are named `IngestEndpointTests`: the mocked one under `Features/Knowledge`, and the Alba one under `E2E`. Filter by namespace. `dotnet test` on a laptop without Docker is not a green suite.

### 2026-11-21 — Global BotPhoneNumberId unique index

- `TenantProfileBotPhoneUniqueIndexTests.BotPhoneNumberId_IsGloballyUnique_AndSameTenantUpsertSucceeds` stores through a Marten lightweight session, not the register endpoint. `tenant-a` and `tenant-b` were absent, so those ids were used. Phones are `phone-{guid:N}`.
- A duplicate phone throws `JasperFx.DocumentAlreadyExistsException` whose inner `PostgresException` is SqlState 23505 and constraint `mt_doc_tenantprofile_uidx_bot_phone_number_id`. Asserting `PostgresException` directly fails.
- `DeployedDatabaseSchemaTests.Baseline_script_matches_marten_configuration` now applies `20261026_001` and then `20261121_001` before `AssertDatabaseMatchesConfigurationAsync`.
- `dotnet test` could not start `AutomaticEnvelopes.Tests.exe` (Win32 4551, Application Control). The same FullyQualifiedName filter passed via `dotnet exec` on the test dll with `-filterVSTest`: 3 passed, 0 failed.
