# ✉️ Automatic Envelopes: Multi-Tenant AI Assistant Platform

Built with a **"Quality-First"** mindset by a former QA turned Backend Engineer.

> "I spent 7 years learning how software breaks so I could spend the rest of my career building systems that don't."

---

## 🚀 Overview
**Automatic Envelopes** is an advanced, scalable AI assistant platform designed to automate complex business logic, customer service, and customized user interactions via WhatsApp for multiple businesses simultaneously.

This project isn't just a bot; it's a showcase of **Modern .NET Engineering** tailored for a SaaS environment. It moves away from traditional CRUD/REST patterns toward a highly resilient, event-driven architecture that prioritizes data integrity, strict multi-tenant isolation, and developer experience.

## 🛠️ The Tech Stack
Built with the cutting-edge **.NET 10** (Preview) and **C# 14**, Automatic Envelopes leverages the "Critter Stack":

- **Marten**: Document DB and Event Store on top of PostgreSQL. Every interaction is recorded as a domain event, ensuring a perfect audit trail segregated by tenant.
- **Wolverine**: The next-gen "Message Bus" and "Mediator". It handles complex asynchronicity with elegant cascading messages and outbox patterns.
- **Microsoft.Extensions.AI**: A unified, provider-agnostic SDK for LLM integration (Ollama, OpenAI, Gemini).
- **pgvector**: Integrated directly into Marten for lightning-fast RAG (Retrieval-Augmented Generation). By feeding the AI with **verified, client-specific official documentation**, we ensure the assistants provide factual, accurate information while strictly preventing AI hallucinations—a clinical application of the "Quality-First" philosophy.

## 🛡️ Quality as a First-Class Citizen
Coming from a QA background, I believe that **untested code is legacy code**.
- **Alba**: For full-stack, in-memory component testing of the entire HTTP/Wolverine pipeline.
- **Testcontainers**: To guarantee that integration tests run against real, ephemeral PostgreSQL instances in Docker—no mocks, no "it works on my machine."
- **Vertical Slice Architecture**: Features are organized by business value, not by technical layers (Controllers/Services/Repos), reducing cognitive load and making the system exceptionally easy to scale for new clients.

## Local database

`dotnet run` uses the Development environment from `Properties/launchSettings.json`. The local Marten connection string (`Host=localhost;Port=5433;Database=automaticenvelopes`) is in `appsettings.Development.json`, which ASP.NET Core loads only for that environment. The base `appsettings.json` has no database password.

Deployed Lambdas still take the database password from AWS Secrets Manager. That secret is the `marten_app` login, not the RDS master user. Apply the Database workflow before the API workflow so the login exists. Marten schema changes are versioned SQL in `db/migrations` and are applied as the RDS master user by the Database workflow and, before each API apply, by the API workflow (plan, approve, apply). Deployed Lambdas do not change the schema on startup. The API workflow applies production serverless before `infra/api-delegation`, which then writes that account's three SES DKIM CNAMEs into the development zone `core-webhook.eu` and writes none until three tokens exist. `AddAwsSecureConfiguration` writes `ConnectionStrings:Marten` after the JSON files when `SECRET_ARN_MARTEN` and `DB_HOST` are both set, so that value is the one Marten uses. The dev Lambda's `ASPNETCORE_ENVIRONMENT` is `Development` and the prod Lambda's is `Production`. Tests set `ConnectionStrings__Marten` to the Testcontainers connection string, which overrides the JSON files.

## 🏗️ Architecture: The Event Pipeline
Automatic Envelopes operates on a reactor-like, tenant-aware event pipeline:
1. **Webhook Intake**: Validates WhatsApp HMAC signatures, resolves the correct client (tenant context), and persists the raw intake.
2. **NLP Detection**: Uses AI to identify language and user intent dynamically.
3. **RAG Retrieval**: Vector searches the isolated knowledge base to find contextually relevant information exclusively for that specific business.
4. **Persona Generation**: Crafts a customized response using the unique brand persona, tone, and system prompts configured for the client.
5. **Dispatch**: Safely sends the response back to the user via the Meta Cloud API.

## Admin portal authentication

The admin portal (Cognito Hosted UI + PKCE) exchanges the authorization code on the API. The browser never sees the client secret, and tokens are not returned in JSON.

| Method | Path | Auth | Body / response |
| --- | --- | --- | --- |
| `POST` | `/auth/token` | anonymous | `{ code, codeVerifier, redirectUri }` → `{ email, name, groups }` plus session cookies |
| `GET` | `/me` | access cookie or Bearer, and the `ae_id` cookie | `{ email, name, groups }` from the ID token only |
| `POST` | `/auth/logout` | anonymous, `Origin` must be an allowed portal origin | clears the session cookies and returns `{ cognitoLogoutUrl }` |

Cookies (host-only, `Path=/`, `HttpOnly`, `Secure`, `SameSite=Lax`):

`SameSite=Lax` cookies are sent on credentialed fetches when the portal and the API share a site. The portal is built with `https://api.core-webhook.eu` in production and `https://api.dev.core-webhook.eu` in development. Production serves that name from its own Route 53 zone. Development publishes the delegation and its own API name in `core-webhook.eu`. A `*.lambda-url.on.aws` host is a different site, so the browser will not attach these cookies there. The WhatsApp webhook stays on the function URL.

- `ae_access` (`Path=/`) — Cognito access token. JwtBearer reads it when the `Authorization` header is absent.
- `ae_id` (`Path=/me`) — Cognito ID token, used only by `GET /me` for email, name, and `cognito:groups`.

Access tokens are checked against `client_id` (they have no `aud` claim). ID tokens are checked against `aud`. `token_use` must match. JwtBearer validates access tokens with `JsonWebTokenHandler`, so `client_id` and `token_use` are read from either a `JsonWebToken` or a `JwtSecurityToken`.

`GET /me` returns email, name, and groups only from the `ae_id` cookie. That ID token's `sub` must match the access token `sub` (or the inbound nameidentifier claim). Access tokens do not carry email or name, and claims on the access token are not used as a profile. A Bearer-only or `ae_access`-only call returns 401. The portal must send both cookies with `credentials: include`.

`POST /auth/logout` only clears the API cookies. It does not call Cognito `/oauth2/revoke`, and the refresh token is not stored. The Cognito Hosted UI session stays alive until the browser navigates to `cognitoLogoutUrl`. That URL is `https://{COGNITO_DOMAIN}/logout?client_id={COGNITO_CLIENT_ID}&logout_uri={sign-out url}` for the logout URL whose origin matches the request `Origin`. The portal must redirect there after logout. A cross-site form post is rejected (403, cookies kept) unless `Origin` is an exact portal origin.

`AdminPolicy` (10 requests/minute) still applies only to tenant registration and document ingest. Each caller and route has its own window: the JWT `sub` (or the inbound name identifier), or the client IP when there is no subject, plus the request path with any trailing slash removed and the path lowercased. `/API/admin/ingest/acme` and `/api/admin/ingest/acme/` share that window. `AuthPolicy` uses the same path normalization. `/auth/*`, `/me`, and `/tenants` use `AuthPolicy` (60 requests/minute per caller and path). Both limiters keep their counters in memory on the Lambda instance that handled the request. A cold start or another instance has its own window. That limit is not a substitute for Cognito's own throttling. The Cognito app client does not reject `/oauth2/authorize` requests that omit PKCE. The portal must send `code_challenge_method=S256` on authorize. `POST /auth/token` always requires `codeVerifier`. This slice does not change the user pool client.

### Environment variables

Set on the API Lambda. Lists are comma-separated exact values. `*` is rejected.

| Variable | Purpose |
| --- | --- |
| `COGNITO_USER_POOL_ID` | Issuer for JWT validation (`https://cognito-idp.{AWS_REGION}.amazonaws.com/{poolId}`) |
| `COGNITO_CLIENT_ID` | Public app client used for the code exchange and access-token `client_id` check |
| `COGNITO_DOMAIN` | Hosted UI host with no scheme, for `POST /oauth2/token`. Example: `automatic-envelopes-admin-prod.auth.eu-west-1.amazoncognito.com` |
| `COGNITO_ALLOWED_REDIRECT_URIS` | Exact `redirect_uri` values accepted by `POST /auth/token` |
| `COGNITO_LOGOUT_URIS` | Exact Cognito sign-out URLs. `POST /auth/logout` returns the one whose origin matches the request `Origin` |
| `ADMIN_PORTAL_ORIGINS` | Exact browser origins allowed with credentials, and the only `Origin` values that may call `POST /auth/logout` |
| `AWS_REGION` | Region segment of the issuer (already used elsewhere; default `eu-west-1`) |

Allowlists are not compiled into the API. Local `appsettings.json` supplies `AdminAuth:AllowedOrigins`, `AdminAuth:AllowedRedirectUris`, and `AdminAuth:AllowedLogoutUris`. Deployed Lambdas replace those with `ADMIN_PORTAL_ORIGINS`, `COGNITO_ALLOWED_REDIRECT_URIS`, and `COGNITO_LOGOUT_URIS`. If no exact origin is configured, the API does not start. Cognito logout URLs are the portal login pages (`/admin/login` locally, `/login` in production), which is where Hosted UI returns after logout.

## Admin portal tenants

Same access token as the rest of the API: the `ae_access` cookie, or `Authorization: Bearer` when the header is present. These routes do not read `ae_id`. That cookie is `Path=/me`, and `cognito:groups` is already on the access token. CORS is the admin-portal policy (`credentials` allowed for the configured origins, including `http://localhost:5173`).

Group `admin` may read and update every tenant. Any other group is a tenant id. A caller whose only group is that id receives only that tenant. `admin` wins when both are present. The same rule is `TenantAccess.CanAccess`, which `TenantAdmin` uses for `POST /api/admin/tenants/{tenantId}` and `POST /api/admin/ingest/{tenantId}`. Path ids must match `^[a-z0-9-]+$` and be at most 64 characters.

| Method | Path | Auth | Body / response |
| --- | --- | --- | --- |
| `GET` | `/tenants` | access cookie or Bearer | `TenantProfile[]` for the caller's groups. An allowed caller with no matching documents gets `[]`. |
| `GET` | `/tenants/{tenantId}` | same | one `TenantProfile`. `403` when the group does not allow it (whether or not the document exists). `404` when it is allowed and missing. `400` when the id is not a valid path id. |
| `PATCH` | `/tenants/{tenantId}` | same | `{ systemPrompt, privacyPolicyUrl, enabledTools?, monthlyResponseLimit?, contactEmail? }` → updated profile. Same authz. `privacyPolicyUrl` must be `https`, or `http` on `localhost`, `127.0.0.1`, or `::1`, with no user info. `systemPrompt` is required. Only a platform admin may send `enabledTools`, `monthlyResponseLimit`, or `contactEmail`; a club role that sends any of them gets 403. Omit a field to leave it unchanged. `enabledTools: []` clears the list. A name that is not on the API allowlist is 400. `monthlyResponseLimit` is an integer from 0 to 100000. A missing stored limit behaves as 1000. `contactEmail: ""` clears the address. |
| `GET` | `/tenants/{tenantId}/usage` | same | `{ month, responses, tokens: { input, output }, limit }` for the current Europe/Madrid calendar month. `responses` is 0 when nothing was metered yet. `limit` is the stored cap, or 1000 when the document has none. `404` when the tenant document is missing. |
| `GET` | `/tenants/{tenantId}/dashboard` | same | Summary for the portal panel: counts, 14-day activity, recent threads. `404` when the tenant document is missing. |
| `GET` | `/tenants/{tenantId}/conversations` | same | Threads for that tenant. Query: `q`, `language`, `from`, `to` (`yyyy-MM-dd`), `includeAnonymized` (`true` when omitted). |
| `GET` | `/tenants/{tenantId}/conversations/{conversationId}` | same | One thread. Phone numbers are not used as the path id; the id is `[a-z0-9-]`. `language` is `null` because the bot does not classify it. `topic` is the opening user message. |
| `GET` | `/tenants/{tenantId}/documents` | same | Files indexed for that tenant. |
| `POST` | `/tenants/{tenantId}/documents` | same, `AdminPolicy` | Multipart field `file` (`.pdf`, `.md`, `.txt`), at most 4 MiB, 100 PDF pages, and 200000 characters. Larger files return 413: `The file is larger than 4 MiB (4194304 bytes).` Too many pages or characters return 400. Indexes that file and returns the document. A second upload of the same filename replaces its chunks. |
| `DELETE` | `/tenants/{tenantId}/documents/{documentId}` | same | Removes the file and its chunks. `204`. |

`POST /api/admin/tenants/{tenantId}` is unchanged. `POST /api/admin/ingest/{tenantId}` still ingests a file and now keeps other files for that tenant instead of clearing the whole knowledge base. The portal uses `/tenants/{tenantId}/documents`. Both upload actions use the same 4 MiB, 100-page, and 200000-character checks.

The 413 is the `file.Length` check. `[RequestSizeLimit]` is on both actions for local Kestrel. On Lambda, `Amazon.Lambda.AspNetCoreServer` does not set `IHttpMaxRequestBodySizeFeature`, so that attribute does not reject the body. Lambda's synchronous invocation payload is 6 MB. A Function URL base64-encodes a binary body inside that payload, which puts the practical file ceiling near 4.5 MB. 4 MiB stays under that.

The page and character caps limit how much text is stored. They do not limit parsing cost. PdfPig decompresses PDF streams with no output cap while it counts pages and extracts text, so a file under 4 MiB can still be expensive to parse on the HTTP Lambda. A possible follow-up is to move that extraction onto the existing SQS worker. This change does not do that, and it does not add a queue or any other resource.

Portal JSON (camelCase). `createdAt` stays on the Marten document and is not returned.

```json
{
  "id": "example-tenant",
  "name": "Example",
  "shortName": "Example",
  "city": "",
  "kind": "",
  "botPhoneNumberId": "109283746510293",
  "displayPhone": "",
  "systemPrompt": "...",
  "privacyPolicyUrl": "https://example.com/privacy",
  "enabledTools": null,
  "monthlyResponseLimit": 1000,
  "contactEmail": ""
}
```

`Name`, `ShortName`, `City`, `Kind`, and `DisplayPhone` are portal labels stored on the Marten document. The bot never reads them. There is no catalog and no slug humanizing: a blank stored value is returned as `""`. The portal shows the id when `name` or `shortName` is blank.

| Stored field | JSON field | When the stored value is blank |
| --- | --- | --- |
| `Id` | `id` | required; used as stored |
| `Name` | `name` | `""` |
| `ShortName` | `shortName` | `""` |
| `City` | `city` | `""` |
| `Kind` | `kind` | `""` |
| `BotPhoneNumberId` | `botPhoneNumberId` | used as stored. This is still the Meta phone-number id the webhook routes on |
| `DisplayPhone` | `displayPhone` | `""`. Portal label, not a WhatsApp route |
| `SystemPrompt` | `systemPrompt` | used as stored, including empty. The bot keeps its own fallback |
| `PrivacyPolicyUrl` | `privacyPolicyUrl` | used as stored. A legacy `http` URL is returned as text; only a new `PATCH` must pass the https / localhost check |
| `EnabledTools` | `enabledTools` | `null` when the document has no list. The bot then attaches no tools. `[]` is an explicit clear |
| `MonthlyResponseLimit` | `monthlyResponseLimit` | `1000` when the document has no value. The JSON field is always that effective number |
| `ContactEmail` | `contactEmail` | `""`. Platform admin sets it. Usage alerts go there |
| `CreatedAt` | omitted | unchanged on `PATCH` |

A stored display value is trimmed and returned as stored. `PATCH` writes `systemPrompt` and `privacyPolicyUrl`. It writes `enabledTools`, `monthlyResponseLimit`, and `contactEmail` only when a platform admin sends the field. Display fields and the bot phone id stay as they are. Omitting `enabledTools` does not clear a list already stored, and a prompt-only save does not turn a missing list into `[]`. Omitting `monthlyResponseLimit` or `contactEmail` leaves the stored value in place.

The bot counts one reply per answered WhatsApp turn, plus the Bedrock input and output tokens of that reply, in one Marten document per tenant and Europe/Madrid month. At the limit it does not call the model: the reply is one Spanish sentence, and a first contact gets the Spanish notice with no translation. `BORRAR DATOS` still runs. The worker emails `contactEmail` from `noreply@core-webhook.eu` at 80% and at 100%, once each per month, and only remembers the send after SES accepts it. Raising `monthlyResponseLimit` clears those flags for the current month. The worker reads `Usage:PhoneHashKey` from Secrets Manager (`SECRET_ARN_USAGE`) and `Usage__FromAddress` from its environment. The per-user spam shield key is an HMAC of the tenant id and the phone, not the phone. A missing HMAC key fails the message so it can be retried.

The shared system prompt keeps the security rules first. The tenant `systemPrompt` is placed after them, inside a delimited block, and cannot override those rules. No tool is registered, so the allowlist is empty: any name is rejected, and the bot attaches nothing until a tool is added to that list. A missing or empty `enabledTools` also attaches nothing. Retrieval is not a tool.