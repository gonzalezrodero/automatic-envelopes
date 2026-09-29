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

`SameSite=Lax` cookies are sent on credentialed fetches when the portal and the API share a site, such as `admin.core-webhook.eu` and `api.core-webhook.eu`. A `*.lambda-url.on.aws` host is a different site, so the browser will not attach these cookies there. Point the portal at a same-site API host for a cookie session.

- `ae_access` (`Path=/`) — Cognito access token. JwtBearer reads it when the `Authorization` header is absent.
- `ae_id` (`Path=/me`) — Cognito ID token, used only by `GET /me` for email, name, and `cognito:groups`.

Access tokens are checked against `client_id` (they have no `aud` claim). ID tokens are checked against `aud`. `token_use` must match. JwtBearer validates access tokens with `JsonWebTokenHandler`, so `client_id` and `token_use` are read from either a `JsonWebToken` or a `JwtSecurityToken`.

`GET /me` returns email, name, and groups only from the `ae_id` cookie. That ID token's `sub` must match the access token `sub` (or the inbound nameidentifier claim). Access tokens do not carry email or name, and claims on the access token are not used as a profile. A Bearer-only or `ae_access`-only call returns 401. The portal must send both cookies with `credentials: include`.

`POST /auth/logout` only clears the API cookies. It does not call Cognito `/oauth2/revoke`, and the refresh token is not stored. The Cognito Hosted UI session stays alive until the browser navigates to `cognitoLogoutUrl`. That URL is `https://{COGNITO_DOMAIN}/logout?client_id={COGNITO_CLIENT_ID}&logout_uri={sign-out url}` for the logout URL whose origin matches the request `Origin`. The portal must redirect there after logout. A cross-site form post is rejected (403, cookies kept) unless `Origin` is an exact portal origin.

`AdminPolicy` (10 requests/minute) still applies only to tenant registration and document ingest. `/auth/*` and `/me` use `AuthPolicy` (60 requests/minute per caller and path). That limit is not a substitute for Cognito's own throttling. The Cognito app client does not reject `/oauth2/authorize` requests that omit PKCE. The portal must send `code_challenge_method=S256` on authorize. `POST /auth/token` always requires `codeVerifier`. This slice does not change the user pool client.

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