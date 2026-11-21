# Infra — History

## Core Context

- **Role:** DevOps Engineer
- **Joined:** 2026-11-21

## Learnings

### 2026-11-21 — Onboarding

#### Architecture

Two accounts, one region for stacks (`eu-west-1`). Development and production each have their own state bucket. Stacks never read the other account's state. Cross-account data (production name servers and SES DKIM tokens) is passed as workflow variables by `reusable-api-domain.yml`.

Every stack's `backend "s3"` block is empty. CI fills it with `-backend-config=config/<dev|prod>/config.remote`. Remote keys are `bucket`, `key`, `region`, `encrypt`, `use_lockfile`. Bucket name pattern is `automatic-envelopes-tf-state-<account>`. Object keys:

| Stack | State key | Config present |
| --- | --- | --- |
| `infra/bootstrap` | `bootstrap/terraform.tfstate` | dev and prod |
| `infra/network` | `network/terraform.tfstate` | dev and prod |
| `infra/db` | `db/terraform.tfstate` | dev and prod |
| `infra/serverless` | `serverless/terraform.tfstate` | dev and prod |
| `infra/portal` | `portal/terraform.tfstate` | dev and prod |
| `infra/api-dns` | `api-dns/terraform.tfstate` | prod only |
| `infra/api-delegation` | `api-delegation/terraform.tfstate` | dev only |

`use_lockfile` is true. Workflows pin Terraform `1.17.0`. AWS provider is `~> 6.0`. `infra/db` also requires Terraform `>= 1.17.0`, `random ~> 3.7`, and `cyrilgdn/postgresql ~> 1.25`.

**`infra/bootstrap`** creates the state bucket and `automatic-envelopes-tf-state-logs-<account>` (public access blocked, HTTPS-only, `force_destroy` false). It creates the GitHub OIDC provider and `automatic-envelopes-github-deploy-role` (trust: `ref` is `main`, `sub` is the `development` or `production` environment). The plan role is created only when `create_plan_role` is true (dev tfvars). It also keeps an ECS task role and a Bedrock invoke policy. There is no ECS service in these stacks; Lambdas attach that same policy.

**`infra/network`** is `terraform-aws-modules/vpc/aws` `6.7.3`. Two AZs, DNS on, NAT off. Dev CIDR `10.0.0.0/16`, prod `10.1.0.0/16`. Private `/24`s are offsets 1–2; public `/24`s are 101–102. Outputs: `vpc_id`, `private_subnet_ids`, `public_subnet_ids`.

**`infra/db`** reads network remote state (`network/terraform.tfstate`, region hardcoded `eu-west-1`). It creates subnet group `${project_name}-db-subnet-group` on the **public** subnets, parameter group `${project_name}-postgres16` (`rds.force_ssl=1`), and RDS `${project_name}-db` (PostgreSQL 16, database `automaticenvelopes`, master user `dbadmin`, `manage_master_user_password`, encrypted gp3, max storage 100, Performance Insights and Enhanced Monitoring off). The monitoring role exists and is not attached. Security group `${project_name}-db-sg` allows TCP 5432 from `0.0.0.0/0` because the Lambdas are outside the VPC and there is no NAT. `publicly_accessible` is `ignore_changes`. EventBridge Scheduler group `${project_name}-rds` stops the instance at 22:00 Europe/Madrid. The 08:00 start stays disabled while `rds_auto_start` is false (both tfvars). `marten_app` is a PostgreSQL login plus grants, with the password in Secrets Manager `${project_name}/marten-app` (write-only). Outputs: `db_endpoint`, `db_password_secret_arn`.

**`infra/serverless`** reads `db/terraform.tfstate` and `bootstrap/terraform.tfstate`. It owns ECR `${project_name}-api` (immutable tags, KMS, scan on push, `force_delete`, keep last 30 images), two container Lambdas on that one image (`${project_name}-api` command `AutomaticEnvelopes.Api`; `${project_name}-worker` command `AutomaticEnvelopes.Api::AutomaticEnvelopes.Api.SqsLambdaHandler::FunctionHandler`), a function URL with `authorization_type` NONE, SQS `${project_name}-messages-queue` and `${project_name}-system-queue` plus DLQ `wolverine-dead-letter-queue`, Cognito pool `automatic-envelopes-admin-pool` (admin-create only, public SPA client, hosted-UI prefix from `cognito_auth_domain`), SES identity `core-webhook.eu`, and secret `${project_name}/phone-hash-key` (worker only). Dev sets `api_hostname` (`api.dev.core-webhook.eu`) and `dns_zone_name` (`core-webhook.eu`), so this stack calls module `api-edge` and writes three DKIM CNAMEs in the parent zone. Prod leaves `api_hostname` empty, so it creates the SES identity and skips the module and the DKIM records. Lambda env points at `SECRET_ARN_MARTEN`, `DB_HOST`, and SSM path `/automatic-envelopes/whatsapp/`. The worker also gets `SECRET_ARN_USAGE` and `Usage__FromAddress` `noreply@core-webhook.eu`.

**`infra/modules/api-edge`** has no backend. Callers pass providers `aws.use1` (us-east-1) and `aws.dns`. It creates the ACM certificate (must be us-east-1), DNS validation, a CloudFront distribution (caching disabled, `Managed-AllViewerExceptHostHeader`, PriceClass_100, TLS 1.2) in front of the Lambda function URL, alias A/AAAA records, and log bucket `automatic-envelopes-api-logs-<caller account>` in us-east-1. One instance per account: dev via `infra/serverless`, prod via `infra/api-dns`.

**`infra/api-dns`** (production account) creates a Route 53 zone named `api.core-webhook.eu` and calls `api-edge`, origin from production serverless `lambda_url_raw`. Outputs `api_base` and `name_servers`.

**`infra/api-delegation`** (development account) looks up parent zone `core-webhook.eu` and publishes an NS record for `api.core-webhook.eu` plus the production DKIM CNAMEs. It writes CNAMEs only when `ses_dkim_tokens` has length 3.

**`infra/portal`** reads `serverless/terraform.tfstate`. Cloudflare Pages project (dev `automatic-letters-admin-dev`, prod `automatic-letters-admin`) builds `admin/` from GitHub repo `automatic-letters-web`, production branch `main`, preview deployments off. Build env is plain `VITE_API_BASE`, `VITE_COGNITO_DOMAIN`, `VITE_COGNITO_CLIENT_ID`, `VITE_BASE`, `NODE_VERSION`. The parent zone lives in development, so only the dev stack sets `dns_zone_name` and `dns_cname_records` (portal hostnames to `pages.dev`). Prod portal tfvars leave DNS empty. `cloudflare_api_token` and `cloudflare_account_id` are ephemeral inputs from GitHub, not tfvars keys.

Parent zone `core-webhook.eu` stays in development. Dev API and both portal CNAMEs are records in that zone. Production API is a delegated child zone.

**Workflows (file → job id, display name).** Apply, plan, and approval already live here. Inner jobs share a concurrency group per stack and environment with `cancel-in-progress: false`.

- `bootstrap.yml` (`CI/CD: Bootstrap`) — `deploy-dev` (development), `deploy-prod` (production). Calls `reusable-terraform.yml` with `working_dir: infra/bootstrap`. Group `deploy-bootstrap-<ref>`.
- `network.yml` (`CI/CD: Network`) — `deploy-dev`, `deploy-prod`. Group `deploy-network-<ref>`.
- `db.yml` (`CI/CD: Database`) — `deploy-dev` (development), `migrate-dev` (migrate development), `deploy-prod` (production), `migrate-prod` (migrate production). Terraform via `reusable-terraform.yml`; schema via `reusable-db-migrate.yml`. Group `deploy-db-<ref>`.
- `deploy.yml` (`CI/CD: API`) — `build-and-test` (Build and test), `migrate-dev` (migrate development), `deploy-dev` (development, `reusable-serverless.yml`), `migrate-prod` (migrate production), `deploy-prod` (production), `deploy-api-domain` (production API domain, `reusable-api-domain.yml`). Paths include `src/**`, `tests/**`, `Dockerfile`, `infra/serverless/**`, `infra/modules/**`, `infra/api-dns/**`, `infra/api-delegation/**`, and `db/migrations/**`. Group `deploy-serverless-<ref>`.
- `portal.yml` (`CI/CD: Portal`) — `deploy-dev` (development), `deploy-prod` (production) via `reusable-portal.yml`. Group `deploy-portal-<ref>`.
- `reusable-terraform.yml` (`Template: Terraform Plan & Apply`) — `plan` (Plan), `approve` (Approve), `apply` (Apply). For `infra/db` only, Plan and Apply fetch the RDS-managed master secret at runtime and mask it.
- `reusable-serverless.yml` (`Template: Build, Plan & Apply Serverless`) — `plan` (Plan), `approve` (Approve), `apply` (Apply). Plan computes image tag `YYYY.MM.DD.<run_number>`, can adopt ECR and existing Lambda log groups, and saves `tfplan`. Apply builds the root `Dockerfile`, pushes `automatic-envelopes-api:<tag>`, then applies that plan. Apply `needs` both `plan` and `approve` so the tag output survives the approval job.
- `reusable-portal.yml` (`Template: Terraform Plan & Apply Portal`) — `plan` (Plan), `approve` (Approve), `apply` (Apply). Apply then asks Cloudflare to deploy the Pages project.
- `reusable-approve.yml` (`Template: Manual approval`) — `approve` (Approve) on environment `<env>-apply` (`development-apply`, `production-apply`).
- `reusable-db-migrate.yml` (`Template: Database migrations`) — `plan` (Plan), `approve` (Approve), `apply` (Apply). Runs `.github/scripts/db-migrate.sh` as the RDS master user. Group `db-migrate-<env>`. This is Backend's schema path, not a Terraform stack.
- `reusable-api-domain.yml` (`Template: Production API domain`) — `plan` (Plan), `approve` (Approve), `apply-zone` (Create zone, `-target=aws_route53_zone.api`, then reads `name_servers` and production `ses_dkim_tokens`), `delegate` (Delegate, development environment, applies `infra/api-delegation`), `apply` (Apply, full `infra/api-dns`). These jobs set no concurrency group. Plan does not save a plan file.
- `pr-terraform.yml` (`CI/CD: Pull request plan`) — `changes` (Detect stacks), `plan` (Plan). Same-repo PRs only. Dev account only, plan role from dev bootstrap, `-lock=false`. Matrix: `bootstrap`, `network`, `db`, `serverless`, `portal`. `infra/api-dns` and `infra/api-delegation` are not on this matrix. DB plans pass `-refresh=false` and do not set the master password. Serverless plans pass `image_tag=pr-<number>`.
- `destroy.yml` (`CI/CD: Destroy`) — `confirm` (Confirm), `approve` (Approve), `destroy` (Destroy). Manual, confirmation `yes`, choices `infra/serverless`, `infra/db`, `infra/network`, `infra/bootstrap`, `infra/portal`. Same `tf-<folder>-<env>` lock as the apply templates. It does not list `api-dns` or `api-delegation`.
- `pr-check.yml` — job id `build-test` (display name Build and test). `sonarcloud.yml` — job id `sonarcloud`. Leave both ids, and `build-and-test`, as they are.

Deploy role trust matches environments `development` and `production` on `main`. Approval is a second environment, `<env>-apply`.

#### Conventions

- Edit the stack that already owns the resource. Dev custom domain and dev DKIM stay in `infra/serverless` (`api_edge.tf`, `ses.tf`). Production zone and CloudFront stay in `infra/api-dns` plus module `infra/modules/api-edge`. Parent NS and production DKIM CNAMEs stay in `infra/api-delegation`. Portal hostnames in the parent zone stay in the **dev** `infra/portal` `dns_cname_records` map.
- Read `config/<env>/` before changing a variable. `config.remote` is backend only. `image_tag` is a CI `-var`, not a tfvars key. `bedrock_model_id` defaults in `variables.tf` (EU Haiku 4.5 profile; allowed rollback `eu.anthropic.claude-sonnet-4-6`). `db_master_password`, `cloudflare_api_token`, and `cloudflare_account_id` are ephemeral and are injected by the workflow.
- Naming already in use: `${project_name}` is `automatic-envelopes`. RDS identifier `${project_name}-db`. Lambdas `${project_name}-api` and `${project_name}-worker`. Queues `${project_name}-messages-queue` and `${project_name}-system-queue`. The DLQ name `wolverine-dead-letter-queue` is not prefixed. Secrets are `${project_name}/marten-app` and `${project_name}/phone-hash-key`.
- Secrets stay write-only. `marten_app` and the phone HMAC use `ephemeral.random_password` plus `secret_string_wo`. Rotation is a bump of `marten_app_password_version` or `phone_hash_key_version` in the existing apply. The RDS master password is AWS-managed. PR plans omit it. The apply workflow reads it into a masked `TF_VAR_db_master_password`.
- WhatsApp and Meta values stay in SSM under `/automatic-envelopes/whatsapp/` (IAM already allows `/automatic-envelopes/*`, including per-tenant parameters). A new `aws_ssm_parameter` for those values does not belong in Terraform. Cognito callback, logout, and origin lists are the existing tfvars, not new resources. A tenant group already exists in `cognito.tf`; tenant names do not get copied into squad files.
- `marten_app` grants (CONNECT, schema USAGE/CREATE, table and sequence DML, default privileges) are this stack. A schema change is a SQL file in `db/migrations` owned by Backend, applied by `reusable-db-migrate.yml` and recorded in `ops.schema_migrations`. Application code under `src/` is Backend's. The portal UI source is the other repo (`automatic-letters-web`); this repo only owns the Pages project and the `VITE_*` values.
- CORS for the public API is the ASP.NET pipeline (exact `admin_portal_origins`). The function URL has no CORS block, because that block cannot send credentials.
- `infra/bootstrap` has no `provider "aws"` block. The region is the CI credential region (`AWS_REGION=eu-west-1`). `var.aws_region` still has to stay `eu-west-1`: Bedrock ARNs use it, and the Lambda policy preconditions on it (Haiku destination regions are the Ireland source list).
- GitHub text and these notes stay in English. No secret values, state dumps, or credentials in `.squad/` or a pull request.

#### Key files

- Backends: `infra/bootstrap/backend.tf`, `infra/network/backend.tf`, `infra/db/backend.tf`, `infra/serverless/backend.tf`, `infra/portal/backend.tf`, `infra/api-dns/backend.tf`, `infra/api-delegation/backend.tf`. Each `config/<env>/config.remote` beside them.
- Bootstrap: `s3_state.tf`, `s3_logs.tf`, `github.tf`, `bedrock.tf`, `ecs_task_role.tf`, `variables.tf`, `outputs.tf`. No `providers.tf`.
- Network: `main.tf`, `variables.tf`, `outputs.tf`, `providers.tf`. Tfvars keys: `project_name`, `vpc_cidr`, `aws_region`.
- DB: `rds.tf`, `sg.tf`, `marten_app.tf`, `scheduler.tf`, `logging.tf`, `iam.tf`, `data.tf`, `providers.tf`, `outputs.tf`, `variables.tf`. Tfvars keys: `project_name`, `aws_region`, `aws_account_id`, `db_instance_class`, `multi_az`, `allocated_storage`, `rds_auto_start`; dev also `backup_retention_period`, `deletion_protection`, `skip_final_snapshot`. Prod relies on variable defaults for retention (7), deletion protection (true), and final snapshot (kept).
- Serverless: `ecr.tf`, `lambda_api.tf`, `lambda_worker.tf`, `sqs.tf`, `cognito.tf`, `ses.tf`, `api_edge.tf`, `iam.tf`, `logs.tf`, `data.tf`, `providers.tf`, `outputs.tf`, `variables.tf`. Tfvars keys: `project_name`, `app_environment`, `aws_account_id`, `aws_region`, `cognito_auth_domain`, `admin_ui_callback_urls`, `admin_ui_logout_urls`, `admin_portal_origins`; dev also `api_hostname`, `dns_zone_name`.
- Module: `infra/modules/api-edge/{terraform.tf,variables.tf,acm.tf,cloudfront.tf,dns.tf,logs.tf,outputs.tf}`.
- API DNS: `infra/api-dns/{main.tf,data.tf,providers.tf,variables.tf,outputs.tf}`. Prod tfvars keys: `aws_region`, `aws_account_id`, `api_hostname`.
- Delegation: `infra/api-delegation/{main.tf,variables.tf,backend.tf}`. Dev tfvars keys: `aws_region`, `zone_name`, `record_name`. `name_servers` and `ses_dkim_tokens` are workflow inputs (`ses_dkim_tokens` defaults to empty).
- Portal: `pages.tf`, `dns.tf`, `locals.tf`, `data.tf`, `providers.tf`, `variables.tf`, `outputs.tf`. Tfvars keys: `aws_region`, `aws_account_id`, `pages_project_name`, `portal_hostname`, `cognito_domain`, `vite_api_base`, `github_owner`, `github_repo`; dev also `dns_zone_name`, `dns_cname_records`.
- Image consumed by serverless: repo-root `Dockerfile` (SDK 10 build, Lambda `public.ecr.aws/lambda/dotnet:10` runtime). ECR name in the workflow env is `automatic-envelopes-api`.
- Workflows listed in Architecture. Schema runner: `.github/scripts/db-migrate.sh` (Backend's script; Infra does not own the SQL).

#### Gotchas

- A workstation `terraform apply`, `terraform destroy`, or AWS CLI mutation races the CI lock and can leak credentials into a transcript. The apply path is the workflow that already owns that stack. `destroy.yml` is that path for the five stacks it lists; it is not a local command and it does not cover `api-dns` or `api-delegation`.
- A second pipeline, or an edit to `.github/workflows/` that renames a job or skips `reusable-approve.yml`, bypasses `development-apply` / `production-apply`. Job ids `build-test`, `build-and-test`, and `sonarcloud` stay as they are.
- `db.yml` and `deploy.yml` both call `reusable-db-migrate.yml` when `db/migrations/**` changes. Migrate jobs share `db-migrate-<env>`, but the RDS Terraform apply uses `tf-infra/db-<env>`. A database apply and a migration from the other workflow can overlap. Schema SQL stays in `db/migrations`.
- `reusable-api-domain.yml` has no concurrency group. `apply-zone` runs `terraform init` in `infra/serverless` to read `ses_dkim_tokens`, which takes the production serverless lock. `deploy.yml` orders this after `deploy-prod`. A separate run that inits that state during a serverless apply will block or be blocked. Do not add another reader.
- That domain workflow is three applies, not one saved plan. `apply-zone` targets only `aws_route53_zone.api` because the certificate cannot validate until `delegate` publishes NS in the development account. Collapsing to a single apply fails validation. `name_servers` has no default; applying `infra/api-delegation` outside `delegate` has nothing to publish. A token list whose length is not 3 writes no DKIM CNAMEs and no placeholders.
- PR plans use `-lock=false` and post the log on the PR. They are comments, not the plan artifact Apply uses. `api-dns` and `api-delegation` never get a PR plan. DB PR plans skip refresh, so `postgresql_*` drift is not evidence. The plan role denies `s3:GetObjectVersion` because older state versions held the RDS master password; current object reads stay allowed. Do not copy plan output, state, or that secret into `.squad/` or a pull request.
- The serverless plan job already creates or imports ECR and Lambda log groups when they are missing, using `image_tag=import`, then plans the real calver tag. The variable description says the tag is a git SHA; the workflow tag is `YYYY.MM.DD.<run_number>`. Apply pushes that immutable tag and then applies the saved plan. A second push of the same tag fails. A PR `image_tag=pr-<number>` is not an image that will be deployed.
- Two callers of `api-edge` in one account collide on `automatic-envelopes-api-logs-<account>`. Dev serverless and prod api-dns are the one caller each.
- The DB subnet group must stay on the public subnets the instance already uses. Replacing those subnet ids in place fails. Moving to private subnets needs a new subnet group, and the Lambdas still need a path: NAT is off and they are outside the VPC. `lifecycle.ignore_changes` on `publicly_accessible` is load-bearing. Changing the `0.0.0.0/0` ingress makes Terraform revoke and re-add the rule. `rds.force_ssl` is immediate on the parameter, but a new parameter-group association waits for the maintenance window (`apply_immediately` stays false).
- Dev can destroy the database (`deletion_protection` false, `skip_final_snapshot` true, backup retention 1). Prod keeps protection, a final snapshot, and retention 7 unless those defaults change. The stop schedule runs every night even while `rds_auto_start` is false.
- `infra/serverless` prod and dev each create an SES identity for `core-webhook.eu`. Only the account that owns the parent zone should write DKIM CNAMEs. Dev does that inside `ses.tf`. Prod tokens go through `api-delegation`. Writing prod CNAMEs from the prod account fails because that account does not own `core-webhook.eu`.
- Portal DNS for `admin.core-webhook.eu` and `admin.dev.core-webhook.eu` is the dev portal stack. A prod portal apply does not own those records. Cloudflare credentials stay in GitHub secrets.
- Bootstrap state is the bucket every other stack uses. `force_destroy` is false. The deploy role is AdministratorAccess and the plan role is ReadOnly plus explicit denies. `create_plan_role` stays a dev-only tfvars flag; prod does not create a plan role. PR plans fail until that dev role exists. Do not print role credentials or state.
