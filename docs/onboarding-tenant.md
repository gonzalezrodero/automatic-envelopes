# Onboard a tenant

One checklist for a new customer in dev or prod. Commands assume the AWS CLI is already logged in and `AWS_PROFILE` is that profile. Region is `eu-west-1`.

Dev account `543704476214`. Prod account `149168707361`. The script checks the account before it writes anything.

Nothing in this document is a secret. Do not commit tokens, app secrets, or verify tokens. Do not paste them into a pull request, a ticket, or a shell transcript.

## 1. Choose the tenant id

The tenant id is the Marten document id and the Cognito group name.

Rules, same as `TenantIds.IsValid` in `src/AutomaticEnvelopes.Api/Features/Tenancy/TenantAccess.cs`:

- `^[a-z0-9-]+$`
- 1 to 64 characters
- not `admin` (`admin` is the platform-admin group)

Examples: `club-example`. Not `Club_Example`, not `admin`.

## 2. Meta (manual)

All tenants share one Meta app and one webhook. `app-secret` and `verify-token` stay on that app. Every tenant needs an access token at `/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token`, including a tenant on the owner's WhatsApp Business account (WABA). The same token value may be stored for more than one tenant. Do not create a second Meta app.

1. In WhatsApp Manager, open the existing WABA and add the customer's phone number. Finish the display-name review and the registration code Meta shows.
2. Copy the **Phone number ID**. It is a numeric id. It is not the E.164 number customers dial. That id is `botPhoneNumberId`.
3. Copy the number you want the portal to show. That is `displayPhone`, a label only. The bot does not route on it.
4. Webhook. In the existing Meta app, WhatsApp, Configuration, set the callback URL to the serverless output `whatsapp_webhook_url`. Paste it exactly.

   ```bash
   cd infra/serverless
   terraform init -backend-config=config/prod/config.remote
   terraform output -raw whatsapp_webhook_url
   ```

   Use `config/dev/config.remote` for dev. The value is the Lambda function URL plus `api/whatsapp/webhook`. The function URL is the webhook host. The portal and the admin API use `https://api.core-webhook.eu` (prod) or `https://api.dev.core-webhook.eu` (dev), not the function URL.
5. Verify token: the value already stored in SSM `/automatic-envelopes/whatsapp/verify-token`. A new tenant does not get a new verify token. Do not rotate it here.
6. Subscribe the webhook to the `messages` field.

Until Meta business verification is complete, and until this is a real registered number rather than a test number, WhatsApp only delivers messages for numbers on the Meta allowed list. That is what blocks real customer traffic. The app can be onboarded before that, and it will not answer the public.

## 3. Shared SSM parameters

Lambdas load the parameters directly under `/automatic-envelopes/whatsapp/` at process start (`SSM_PATH_WHATSAPP`). The read is not recursive, so a name under `tenants/` is not part of this load. The startup load also skips `access-token`. The two names:

| Name | Loaded as | Role |
| --- | --- | --- |
| `app-secret` | `WhatsApp:AppSecret` | Checks `X-Hub-Signature-256` on webhook POST. Stays global. It belongs to the Meta app. |
| `verify-token` | `WhatsApp:VerifyToken` | Webhook GET challenge. Stays global. The API process does not start when this is missing or blank (`Program.cs` validates it). The worker does not. |

`botPhoneNumberId` is stored per tenant on the tenant document. It is the only phone number id the bot uses. Meta sends it as `metadata.phone_number_id`. The worker finds the tenant with that id, and the send call puts it in the Graph URL.

One app secret and one verify token serve every tenant. Every tenant has its own access token, including a tenant on the owner's WABA. The same token value may be copied into more than one tenant parameter.

These two parameters are created and rotated by hand. They are not in Terraform, so the secret never enters Terraform state. Do not commit a value and do not print it. Set each shell variable in the same terminal, then run `aws ssm put-parameter`. The names in `$APP_SECRET` and `$VERIFY_TOKEN` are placeholders. `--overwrite` updates a parameter that already exists. Rotation is the same two commands.

```bash
aws ssm put-parameter \
  --name /automatic-envelopes/whatsapp/app-secret \
  --type SecureString \
  --value "$APP_SECRET" \
  --overwrite

aws ssm put-parameter \
  --name /automatic-envelopes/whatsapp/verify-token \
  --type SecureString \
  --value "$VERIFY_TOKEN" \
  --overwrite
```

Confirm each parameter without printing the secret. Omit `--with-decryption`.

```bash
aws ssm get-parameter \
  --name /automatic-envelopes/whatsapp/verify-token \
  --query 'Parameter.{Name:Name,Type:Type,Version:Version}' \
  --output text
```

Repeat for `app-secret`.

The API does not start without `verify-token`. `Program.cs` calls `AddFeatures` with `validateVerifyToken: true`, and `WhatsAppOptions` rejects a missing or blank `VerifyToken` before the process serves traffic. The worker does not run that check. `app-secret` is required to accept a webhook POST. Sending a reply requires that tenant's access token. A missing parameter, an empty value, or an invalid tenant id throws. The log names the tenant id and the parameter path. It does not contain the token.

Lambdas read these two names once, when the process starts (`AddAwsSecureConfiguration`). A shared parameter created or rotated after that stays invisible until a new execution environment starts. Redeploy with the API workflow so `automatic-envelopes-api` and `automatic-envelopes-worker` cold-start. Do not edit Lambda environment variables by hand. Terraform owns those variables, and it does not own these two parameters.

### Per-tenant access token

Every tenant needs `/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token`. That includes a tenant that uses the owner's WABA. Store the same value again for each of those tenants. When the worker sends a reply it reads that parameter from SSM. The value stays in memory for 5 minutes. Creating or rotating it does not need a redeploy. After the cache expires, the next reply uses the new value. If the parameter is missing or empty, the send fails.

`botPhoneNumberId` on the tenant document is the only phone number id used. `app-secret` and `verify-token` stay on the shared path.

```bash
aws ssm put-parameter \
  --name "/automatic-envelopes/whatsapp/tenants/${TENANT_ID}/access-token" \
  --type SecureString \
  --value "$WHATSAPP_ACCESS_TOKEN" \
  --overwrite
```

Confirm without printing the secret. Omit `--with-decryption`.

```bash
aws ssm get-parameter \
  --name "/automatic-envelopes/whatsapp/tenants/${TENANT_ID}/access-token" \
  --query 'Parameter.{Name:Name,Type:Type,Version:Version}' \
  --output text
```

`scripts/onboard-tenant.sh` requires `WHATSAPP_ACCESS_TOKEN` on a real run and stores it with that `put-parameter`. The script never prints the value. A real run stops when the variable is unset.

## 4. Cognito group, portal user, and tenant registration

`scripts/onboard-tenant.sh` does this. It looks up the pool id by the name `automatic-envelopes-admin-pool`. It does not hardcode a pool id. The Hosted UI prefixes `automatic-envelopes-admin-dev` and `automatic-envelopes-admin-prod` are not the pool name.

The group `club-basquet-sama` is already in Terraform (`aws_cognito_user_group.tenant_sama_group` in `infra/serverless/cognito.tf`), together with the `admin` group. This change does not add groups in Terraform. The script creates later tenant groups. If you point the script at `club-basquet-sama`, it sees the existing group and skips create. It does not edit that Terraform resource.

Preview:

```bash
scripts/onboard-tenant.sh --dry-run prod club-example owner@example.com PHONE_NUMBER_ID "Example Club"
```

Real run. `WHATSAPP_ACCESS_TOKEN` is required. Group exists: skip. User exists: skip. The user is still added to the group (safe to repeat). Registration is sent only when `ADMIN_ACCESS_TOKEN` is set. The per-tenant access token is stored before Cognito. The script does not print it.

```bash
export AWS_PROFILE=the-profile-for-this-account
export WHATSAPP_ACCESS_TOKEN
scripts/onboard-tenant.sh prod club-example owner@example.com PHONE_NUMBER_ID "Example Club"
```

`export WHATSAPP_ACCESS_TOKEN` keeps a value already set in the shell. Do not put the token in the command. A tenant on the owner's WABA still needs this variable. The value may be the same one stored for another tenant.

The pool allows admin-created users only. The script calls `admin-create-user` with the email as the username and asks Cognito to email the temporary password. The customer signs in with that email.

Optional body fields, if you want them on the first save: `TENANT_SHORT_NAME` (defaults to the name argument), `TENANT_CITY`, `TENANT_KIND`, `TENANT_DISPLAY_PHONE`, `TENANT_SYSTEM_PROMPT`, `TENANT_PRIVACY_POLICY_URL`. Empty strings are valid. A blank `systemPrompt` is stored; the bot keeps its own fallback. A later portal save of `privacyPolicyUrl` must be `https`, or `http` on localhost.

### Registration call

`POST /api/admin/tenants/{tenantId}` requires a platform admin: Cognito group `admin` (`TenantAccess.PlatformAdminPolicy`). A tenant group alone receives 403. The body is `RegisterTenantRequest`:

```json
{
  "botPhoneNumberId": "PHONE_NUMBER_ID",
  "systemPrompt": "",
  "privacyPolicyUrl": "",
  "name": "Example Club",
  "shortName": "Example Club",
  "city": "",
  "kind": "",
  "displayPhone": ""
}
```

`201` creates the document. `400` when the id is invalid, the phone id is blank, or the id or phone id is already stored. The duplicate text is `Esa organización o ese número ya están registrados. Entra en la que ya existe o usa otros datos.`

The script does not read admin cookies. With no `ADMIN_ACCESS_TOKEN`, it prints the curl and leaves `$ADMIN_ACCESS_TOKEN` unexpanded. With the variable set, it sends the request from a curl config file so the token is not on the command line, and it never writes the token to stdout. Do not run the script under `bash -x`.

How to get the token: sign in at the portal as a user in the group `admin` (`https://admin.core-webhook.eu` in prod, `https://admin.dev.core-webhook.eu` in dev). In the browser devtools, on the API host (`api.core-webhook.eu` or `api.dev.core-webhook.eu`), copy the `ae_access` cookie. That cookie is HttpOnly. Export it as `ADMIN_ACCESS_TOKEN` in a shell you do not trace. The script then sends `Authorization: Bearer`.

When the token is set, the script first calls `GET /tenants/{tenantId}`. `200` means the tenant is already registered and the POST is skipped. `404` means it is missing, and the script POSTs. Any other status stops the script. A second run is safe for Cognito. A phone id that already belongs to a different tenant still returns 400 from the POST, and the script stops after that response.

`AdminPolicy` allows 10 registration requests per minute for that caller.

## 5. Documents

The customer opens the portal, finishes the Cognito invite, and uploads `.pdf`, `.md`, or `.txt` files for that tenant. The portal calls `POST /tenants/{tenantId}/documents` (field `file`, at most 4 MiB). The group on the token is the tenant id, or `admin`.

## 6. Smoke test

From a phone Meta will deliver to (a tester until business verification is done), send a WhatsApp message to the new number.

The API Lambda checks the signature and enqueues the message. The worker loads the tenant with `BotPhoneNumberId` equal to the webhook phone-number id. If that document is missing, the worker logs `Silent abort: No tenant found in PostgreSQL for BotPhoneNumberId` and does not reply.

## 7. Check that it worked

Portal, signed in as the new user or as a platform admin:

- Prod: `https://admin.core-webhook.eu`
- Dev: `https://admin.dev.core-webhook.eu`

The dashboard and the conversation list are `GET /tenants/{tenantId}/dashboard` and `GET /tenants/{tenantId}/conversations`. Same bearer token, or the portal session.

Usage for the current Europe/Madrid month:

```bash
curl --silent --show-error \
  --header "Authorization: Bearer $ADMIN_ACCESS_TOKEN" \
  "https://api.core-webhook.eu/tenants/club-example/usage"
```

Use `https://api.dev.core-webhook.eu` for dev. A new tenant looks like `{"month":"...","responses":0,"tokens":{"input":0,"output":0},"limit":1000}`. `404` means the tenant document is missing.

CloudWatch log groups, 14-day retention:

- `/aws/lambda/automatic-envelopes-api` for the webhook
- `/aws/lambda/automatic-envelopes-worker` for the reply

```bash
aws logs filter-log-events \
  --log-group-name /aws/lambda/automatic-envelopes-worker \
  --filter-pattern '"club-example"' \
  --start-time "$(( ($(date +%s) - 3600) * 1000 ))"
```

Search the same window for `Silent abort` if the message produced no reply.

## What is shared, and what blocks real use

Shared by every tenant: the Meta app, `app-secret`, `verify-token`, the webhook URL, and the two Lambdas. Per tenant: the tenant id, the Cognito group of the same name, the portal user, the Marten profile (`botPhoneNumberId`, prompt, labels), the uploaded documents, and `/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token`. Tenants on the owner's WABA each have that parameter. The value may be the same.

Real traffic stays blocked while Meta business verification is unfinished or the number is still a test number. A missing `verify-token` stops the API from starting. A wrong app secret stays wrong until the shared `put-parameter` command has been run and both Lambdas have started again. A missing or wrong per-tenant access token fails the send. The worker log names the tenant id and `/automatic-envelopes/whatsapp/tenants/{tenantId}/access-token`. Fix it with `put-parameter --overwrite` on that name. The worker picks up a new value within 5 minutes. No redeploy.
