#!/usr/bin/env bash
# Create the Cognito group and portal user for one tenant, then register the
# tenant or print the exact request to send. WhatsApp secrets stay in SSM.
# ADMIN_ACCESS_TOKEN and WHATSAPP_ACCESS_TOKEN are read from the environment
# and never printed.
#
# Usage:
#   scripts/onboard-tenant.sh [--dry-run] <dev|prod> <tenantId> <customerEmail> <botPhoneNumberId> [name]
#
# Requires an already logged-in AWS CLI profile in AWS_PROFILE (real run only).
# The admin user pool name is automatic-envelopes-admin-pool in both accounts.
# automatic-envelopes-admin-dev and automatic-envelopes-admin-prod are the
# Hosted UI domain prefixes, not the pool name. The pool id is looked up.
#
# Optional profile fields (sent only on the registration body; empty if unset):
#   TENANT_SHORT_NAME TENANT_CITY TENANT_KIND TENANT_DISPLAY_PHONE
#   TENANT_SYSTEM_PROMPT TENANT_PRIVACY_POLICY_URL
# TENANT_SHORT_NAME defaults to the name argument.
#
# Required on a real run. Never printed:
#   WHATSAPP_ACCESS_TOKEN
# Stored at /automatic-envelopes/whatsapp/tenants/<tenantId>/access-token
# (SecureString, --overwrite). Required for every tenant, including one on
# the owner's WABA. The same value may be stored for more than one tenant.
# botPhoneNumberId is stored per tenant on the tenant document and is the
# only phone number id used.
set -euo pipefail

if [[ $- == *x* ]]; then
  echo "Refusing to run under bash -x because that would print ADMIN_ACCESS_TOKEN and WHATSAPP_ACCESS_TOKEN." >&2
  exit 1
fi

export AWS_PAGER=""
export AWS_REGION="${AWS_REGION:-eu-west-1}"
export AWS_DEFAULT_REGION="$AWS_REGION"

POOL_NAME="automatic-envelopes-admin-pool"
DRY_RUN=0
POSITIONAL=()

usage() {
  cat <<'EOF'
usage: scripts/onboard-tenant.sh [--dry-run] <dev|prod> <tenantId> <customerEmail> <botPhoneNumberId> [name]

Creates the Cognito group named like the tenant id, invites the portal user,
and adds the user to that group. Registration is POST /api/admin/tenants/{tenantId}
as a platform admin (Cognito group "admin"). If ADMIN_ACCESS_TOKEN is set, the
script sends that request without printing the token. Otherwise it prints the
curl command with $ADMIN_ACCESS_TOKEN unexpanded.

--dry-run prints the AWS and curl commands and does not call AWS or the API.

WHATSAPP_ACCESS_TOKEN is required for a real run. It is stored as a
SecureString at /automatic-envelopes/whatsapp/tenants/<tenantId>/access-token.
The value is not printed. A real run stops when it is unset. botPhoneNumberId
is stored per tenant on the tenant document and is the only phone number id
used.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)
      DRY_RUN=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      POSITIONAL+=("$@")
      break
      ;;
    -*)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
    *)
      POSITIONAL+=("$1")
      shift
      ;;
  esac
done

if [[ ${#POSITIONAL[@]} -lt 4 || ${#POSITIONAL[@]} -gt 5 ]]; then
  usage >&2
  exit 2
fi

ENV_NAME="${POSITIONAL[0]}"
TENANT_ID="${POSITIONAL[1]}"
EMAIL="${POSITIONAL[2]}"
BOT_PHONE="${POSITIONAL[3]}"
TENANT_NAME="${POSITIONAL[4]:-}"

case "$ENV_NAME" in
  dev)
    EXPECTED_ACCOUNT="543704476214"
    API_BASE="https://api.dev.core-webhook.eu"
    ;;
  prod)
    EXPECTED_ACCOUNT="149168707361"
    API_BASE="https://api.core-webhook.eu"
    ;;
  *)
    echo "env must be dev or prod (got: ${ENV_NAME})." >&2
    exit 2
    ;;
esac

# Same rules as TenantIds.IsValid: ^[a-z0-9-]+$, at most 64, and not the
# platform-admin group name.
if [[ ! "$TENANT_ID" =~ ^[a-z0-9-]{1,64}$ || "$TENANT_ID" == "admin" ]]; then
  echo "tenantId must match ^[a-z0-9-]+$, be 1 to 64 characters, and not be admin." >&2
  exit 2
fi

if [[ ! "$EMAIL" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ || ${#EMAIL} -gt 128 ]]; then
  echo "customerEmail must look like an email address and be at most 128 characters." >&2
  exit 2
fi

if [[ -z "$BOT_PHONE" || "$BOT_PHONE" =~ [[:space:]] ]]; then
  echo "botPhoneNumberId must be non-empty and must not contain whitespace." >&2
  exit 2
fi

SHORT_NAME="${TENANT_SHORT_NAME:-$TENANT_NAME}"
CITY="${TENANT_CITY:-}"
KIND="${TENANT_KIND:-}"
DISPLAY_PHONE="${TENANT_DISPLAY_PHONE:-}"
SYSTEM_PROMPT="${TENANT_SYSTEM_PROMPT:-}"
PRIVACY_URL="${TENANT_PRIVACY_POLICY_URL:-}"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required to build the JSON body." >&2
  exit 1
fi

BODY="$(python3 - "$BOT_PHONE" "$SYSTEM_PROMPT" "$PRIVACY_URL" "$TENANT_NAME" "$SHORT_NAME" "$CITY" "$KIND" "$DISPLAY_PHONE" <<'PY'
import json
import sys

bot, prompt, privacy, name, short, city, kind, display = sys.argv[1:]
print(json.dumps({
    "botPhoneNumberId": bot,
    "systemPrompt": prompt,
    "privacyPolicyUrl": privacy,
    "name": name,
    "shortName": short,
    "city": city,
    "kind": kind,
    "displayPhone": display,
}, ensure_ascii=False))
PY
)"

REGISTER_URL="${API_BASE}/api/admin/tenants/${TENANT_ID}"
TENANT_URL="${API_BASE}/tenants/${TENANT_ID}"

print_whatsapp_access_token_command() {
  cat <<EOF
aws ssm put-parameter \\
  --name '/automatic-envelopes/whatsapp/tenants/${TENANT_ID}/access-token' \\
  --type SecureString \\
  --value "\$WHATSAPP_ACCESS_TOKEN" \\
  --overwrite
EOF
}

ensure_whatsapp_access_token() {
  local name="/automatic-envelopes/whatsapp/tenants/${TENANT_ID}/access-token"
  if [[ -z "${WHATSAPP_ACCESS_TOKEN:-}" ]]; then
    echo "WHATSAPP_ACCESS_TOKEN is required. A real run stores it at ${name} and does not print the value." >&2
    print_whatsapp_access_token_command >&2
    exit 1
  fi
  if [[ "$WHATSAPP_ACCESS_TOKEN" == *$'\n'* || "$WHATSAPP_ACCESS_TOKEN" == *$'\r'* ]]; then
    echo "WHATSAPP_ACCESS_TOKEN contains a newline. Refusing to store it." >&2
    exit 1
  fi
  echo "Storing the per-tenant WhatsApp access token in SSM. The value is not printed."
  aws ssm put-parameter \
    --name "$name" \
    --type SecureString \
    --value "$WHATSAPP_ACCESS_TOKEN" \
    --overwrite \
    --output text \
    --query Version
}

print_register_command() {
  echo "Registration was not sent. Save the JSON below as tenant.json and run the curl as a platform admin (Cognito group admin)."
  echo "The token is not printed."
  printf '%s\n' "$BODY"
  cat <<EOF

curl --silent --show-error --request POST '${REGISTER_URL}' \\
  --header "Authorization: Bearer \$ADMIN_ACCESS_TOKEN" \\
  --header 'Content-Type: application/json' \\
  --data @tenant.json
EOF
}

run_or_print() {
  if [[ "$DRY_RUN" -eq 1 ]]; then
    printf 'dry-run:'
    printf ' %q' "$@"
    printf '\n'
    return 0
  fi
  "$@"
}

require_aws() {
  if [[ -z "${AWS_PROFILE:-}" ]]; then
    echo "Set AWS_PROFILE to the already logged-in profile for ${ENV_NAME}." >&2
    exit 1
  fi
  if ! command -v aws >/dev/null 2>&1; then
    echo "aws CLI is required." >&2
    exit 1
  fi
  local account
  account="$(aws sts get-caller-identity --query Account --output text)"
  if [[ "$account" != "$EXPECTED_ACCOUNT" ]]; then
    echo "AWS account ${account} is not the ${ENV_NAME} account (${EXPECTED_ACCOUNT})." >&2
    exit 1
  fi
}

lookup_pool_id() {
  local next="" page ids id
  local -a found=()
  while true; do
    if [[ -n "$next" ]]; then
      page="$(aws cognito-idp list-user-pools --max-results 60 --next-token "$next" --output json)"
    else
      page="$(aws cognito-idp list-user-pools --max-results 60 --output json)"
    fi
    ids="$(python3 -c 'import json,sys; page=json.load(sys.stdin); print("\n".join(pool["Id"] for pool in page.get("UserPools") or [] if pool.get("Name")==sys.argv[1]))' "$POOL_NAME" <<<"$page")"
    if [[ -n "$ids" ]]; then
      while IFS= read -r id; do
        [[ -z "$id" ]] && continue
        found+=("$id")
      done <<<"$ids"
    fi
    next="$(python3 -c 'import json,sys; page=json.load(sys.stdin); print(page.get("NextToken") or "")' <<<"$page")"
    [[ -z "$next" ]] && break
  done
  if [[ ${#found[@]} -ne 1 ]]; then
    echo "Expected one Cognito pool named ${POOL_NAME}, found ${#found[@]}." >&2
    exit 1
  fi
  printf '%s' "${found[0]}"
}

aws_error_is() {
  local file=$1 code=$2
  grep -q "$code" "$file"
}

ensure_group() {
  local pool_id=$1
  local err
  err="$(mktemp)"
  if aws cognito-idp get-group --user-pool-id "$pool_id" --group-name "$TENANT_ID" >/dev/null 2>"$err"; then
    rm -f "$err"
    echo "Cognito group ${TENANT_ID} already exists; skipping create."
    return 0
  fi
  if aws_error_is "$err" "ResourceNotFoundException"; then
    rm -f "$err"
    echo "Creating Cognito group ${TENANT_ID}."
    aws cognito-idp create-group \
      --user-pool-id "$pool_id" \
      --group-name "$TENANT_ID" \
      --description "Portal users for tenant ${TENANT_ID}."
    return 0
  fi
  cat "$err" >&2
  rm -f "$err"
  exit 1
}

ensure_user() {
  local pool_id=$1
  local err
  err="$(mktemp)"
  if aws cognito-idp admin-get-user --user-pool-id "$pool_id" --username "$EMAIL" >/dev/null 2>"$err"; then
    rm -f "$err"
    echo "Portal user ${EMAIL} already exists; skipping invite."
    return 0
  fi
  if aws_error_is "$err" "UserNotFoundException"; then
    rm -f "$err"
    echo "Inviting portal user ${EMAIL}."
    aws cognito-idp admin-create-user \
      --user-pool-id "$pool_id" \
      --username "$EMAIL" \
      --user-attributes "Name=email,Value=${EMAIL}" "Name=email_verified,Value=true" \
      --desired-delivery-mediums EMAIL >/dev/null
    return 0
  fi
  cat "$err" >&2
  rm -f "$err"
  exit 1
}

api_call() {
  local method=$1 url=$2 data_file=${3:-}
  local cfg="${SECRET_DIR}/curl.cfg"
  local out="${SECRET_DIR}/body"
  : >"$out"
  {
    printf 'request = "%s"\n' "$method"
    printf 'url = "%s"\n' "$url"
    printf 'header = "Accept: application/json"\n'
    printf 'header = "Authorization: Bearer %s"\n' "$ADMIN_ACCESS_TOKEN"
    if [[ -n "$data_file" ]]; then
      printf 'header = "Content-Type: application/json"\n'
      printf 'data = "@%s"\n' "$data_file"
    fi
  } >"$cfg"
  chmod 600 "$cfg"
  local code
  if ! code="$(curl --silent --show-error --config "$cfg" --output "$out" --write-out '%{http_code}')"; then
    rm -f "$cfg"
    echo "The API call failed before a status code was returned." >&2
    exit 1
  fi
  rm -f "$cfg"
  printf '%s' "$code"
}

register_tenant() {
  if [[ "$DRY_RUN" -eq 1 || -z "${ADMIN_ACCESS_TOKEN:-}" ]]; then
    print_register_command
    return 0
  fi
  if [[ "$ADMIN_ACCESS_TOKEN" == *$'\n'* || "$ADMIN_ACCESS_TOKEN" == *$'\r'* || "$ADMIN_ACCESS_TOKEN" == *'"'* ]]; then
    echo "ADMIN_ACCESS_TOKEN contains a newline or a double quote. Refusing to send it." >&2
    exit 1
  fi

  umask 077
  SECRET_DIR="$(mktemp -d)"
  # shellcheck disable=SC2064
  trap "rm -rf '$SECRET_DIR'" EXIT
  local body_file="${SECRET_DIR}/body.json"
  printf '%s' "$BODY" >"$body_file"

  local code
  code="$(api_call GET "$TENANT_URL")"
  if [[ "$code" == "200" ]]; then
    echo "Tenant ${TENANT_ID} is already registered; skipping POST."
    return 0
  fi
  if [[ "$code" != "404" ]]; then
    echo "GET ${TENANT_URL} returned ${code}." >&2
    cat "${SECRET_DIR}/body" >&2
    exit 1
  fi

  code="$(api_call POST "$REGISTER_URL" "$body_file")"
  if [[ "$code" != "201" ]]; then
    echo "POST ${REGISTER_URL} returned ${code}." >&2
    cat "${SECRET_DIR}/body" >&2
    exit 1
  fi
  echo "Registered tenant ${TENANT_ID}."
  cat "${SECRET_DIR}/body"
  printf '\n'
}

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "dry-run: no AWS or API calls."
  echo "dry-run: would require AWS_PROFILE for the ${ENV_NAME} account ${EXPECTED_ACCOUNT}."
  echo "dry-run: would look up pool id with: aws cognito-idp list-user-pools --max-results 60 (name ${POOL_NAME})."
  echo "dry-run: a real run requires WHATSAPP_ACCESS_TOKEN and does not print it."
  echo "dry-run: botPhoneNumberId is stored per tenant on the tenant document and is the only phone number id used."
  print_whatsapp_access_token_command
  run_or_print aws cognito-idp create-group --user-pool-id '<pool-id>' --group-name "$TENANT_ID" --description "Portal users for tenant ${TENANT_ID}."
  run_or_print aws cognito-idp admin-create-user --user-pool-id '<pool-id>' --username "$EMAIL" --user-attributes "Name=email,Value=${EMAIL}" "Name=email_verified,Value=true" --desired-delivery-mediums EMAIL
  run_or_print aws cognito-idp admin-add-user-to-group --user-pool-id '<pool-id>' --username "$EMAIL" --group-name "$TENANT_ID"
  print_register_command
  exit 0
fi

if [[ -z "${WHATSAPP_ACCESS_TOKEN:-}" ]]; then
  echo "WHATSAPP_ACCESS_TOKEN is required. A real run stores it at /automatic-envelopes/whatsapp/tenants/${TENANT_ID}/access-token and does not print the value." >&2
  exit 1
fi

require_aws
ensure_whatsapp_access_token
POOL_ID="$(lookup_pool_id)"
echo "Using Cognito pool ${POOL_ID} (${POOL_NAME})."
ensure_group "$POOL_ID"
ensure_user "$POOL_ID"
echo "Adding ${EMAIL} to group ${TENANT_ID}."
aws cognito-idp admin-add-user-to-group \
  --user-pool-id "$POOL_ID" \
  --username "$EMAIL" \
  --group-name "$TENANT_ID"
register_tenant
