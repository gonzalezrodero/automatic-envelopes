#!/usr/bin/env bash
# Apply db/migrations as the RDS master user. Plan prints SKIP or APPLY and the SQL.
# Apply runs the planned files once, recorded in ops.schema_migrations (not granted to marten_app).
set -euo pipefail

psql_at() {
  psql -v ON_ERROR_STOP=1 -X --no-psqlrc -q "$@"
}

psql_scalar() {
  local sql="$1"
  psql_at -tA -c "$sql"
}

valid_ident() {
  local ident="$1"
  [[ "$ident" =~ ^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$ ]]
}

valid_version() {
  local version="$1"
  [[ "$version" =~ ^[0-9]{8}_[0-9]{3}_[a-z0-9_]+\.sql$ ]]
}

declare -A APPLIED=()

load_applied() {
  local exists
  exists="$(psql_scalar "SELECT to_regclass('ops.schema_migrations') IS NOT NULL")"
  if [[ "$exists" != "t" ]]; then
    return
  fi
  while IFS=$'\t' read -r version checksum; do
    [[ -z "$version" ]] && continue
    APPLIED["$version"]="$checksum"
  done < <(psql_at -tA -F $'\t' -c "SELECT version, checksum FROM ops.schema_migrations ORDER BY version")
}

object_exists() {
  local ident="$1"
  if ! valid_ident "$ident"; then
    echo "invalid migration object name: $ident" >&2
    exit 1
  fi
  local exists
  exists="$(psql_scalar "SELECT to_regclass('${ident}') IS NOT NULL")"
  [[ "$exists" == "t" ]]
}

# Extension members (pgvector) are excluded: the migrations do not alter them.
# marten_app-owned objects are listed for the ownership migration. Any other owner aborts.
report_public_owners() {
  local transfer_pending="$1"
  local current rows marker detail
  local -a app_lines=() other_lines=()
  current="$(psql_scalar "SELECT current_user")"
  rows="$(psql_at -tA -F $'\t' <<'SQL'
SELECT marker, format('%s %s (dueño %s)', kind, name, owner)
FROM (
    SELECT CASE WHEN pg_get_userbyid(c.relowner) = 'marten_app' THEN 'app' ELSE 'other' END AS marker,
           CASE c.relkind
               WHEN 'S' THEN 'secuencia'
               WHEN 'v' THEN 'vista'
               WHEN 'm' THEN 'vista materializada'
               WHEN 'i' THEN 'indice'
               WHEN 'I' THEN 'indice'
               ELSE 'tabla'
           END AS kind,
           format('%I.%I', n.nspname, c.relname) AS name,
           pg_get_userbyid(c.relowner) AS owner
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public'
      AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f', 'i', 'I')
      AND pg_get_userbyid(c.relowner) <> current_user
      AND NOT EXISTS (
          SELECT 1 FROM pg_depend d
          WHERE d.classid = 'pg_class'::regclass
            AND d.objid = c.oid
            AND d.deptype = 'e'
      )
    UNION ALL
    SELECT CASE WHEN pg_get_userbyid(p.proowner) = 'marten_app' THEN 'app' ELSE 'other' END,
           'funcion',
           format('%I.%I(%s)', n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)),
           pg_get_userbyid(p.proowner)
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = 'public'
      AND pg_get_userbyid(p.proowner) <> current_user
      AND NOT EXISTS (
          SELECT 1 FROM pg_depend d
          WHERE d.classid = 'pg_proc'::regclass
            AND d.objid = p.oid
            AND d.deptype = 'e'
      )
) found
ORDER BY 1, 2;
SQL
)"
  while IFS=$'\t' read -r marker detail; do
    [[ -z "$marker" ]] && continue
    if [[ "$marker" == "app" ]]; then
      app_lines+=("$detail")
    else
      other_lines+=("$detail")
    fi
  done <<< "$rows"
  if [[ ${#other_lines[@]} -gt 0 ]]; then
    echo "Hay objetos de public cuyo dueño no es ni el maestro (${current}) ni marten_app:" >&2
    printf '%s\n' "${other_lines[@]}" >&2
    exit 1
  fi
  if [[ ${#app_lines[@]} -gt 0 ]]; then
    if [[ "$transfer_pending" != "true" ]]; then
      echo "Hay objetos de marten_app en public y la migración que los traspasa no está pendiente:" >&2
      printf '%s\n' "${app_lines[@]}" >&2
      exit 1
    fi
    echo "Se traspasarán al usuario maestro (${current}):"
    printf '%s\n' "${app_lines[@]}"
    return
  fi
  echo "Dueños: ${current} es dueño de las tablas, secuencias, funciones y vistas de public."
}

ownership_transfer_is_pending() {
  local migrations_dir="$1"
  local file
  shopt -s nullglob
  for file in "$migrations_dir"/*.sql; do
    if grep -q -- '^-- migrate: transfers-ownership-from marten_app$' "$file"; then
      evaluate_file "$file"
      if [[ "$SHOULD_APPLY" == "1" ]]; then
        return 0
      fi
    fi
  done
  return 1
}

# Sets SHOULD_APPLY=1 when this file still needs to run.
evaluate_file() {
  local file="$1"
  local version checksum line
  version="$(basename "$file")"
  SHOULD_APPLY=0
  SKIP_REASON=""
  checksum="$(sha256sum "$file" | awk '{print $1}')"
  FILE_CHECKSUM="$checksum"

  if [[ -v "APPLIED[$version]" ]]; then
    if [[ "${APPLIED[$version]}" != "$checksum" ]]; then
      echo "checksum mismatch for applied migration $version" >&2
      exit 1
    fi
    SKIP_REASON="already applied"
    return
  fi

  local -a missing=() present=() skip_applied=()
  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in
      "-- migrate: apply-if-missing "*)
        missing+=("${line#-- migrate: apply-if-missing }")
        ;;
      "-- migrate: apply-if-present "*)
        present+=("${line#-- migrate: apply-if-present }")
        ;;
      "-- migrate: skip-if-applied "*)
        skip_applied+=("${line#-- migrate: skip-if-applied }")
        ;;
      *)
        ;;
    esac
  done < "$file"

  local ident other
  for ident in "${missing[@]+"${missing[@]}"}"; do
    ident="${ident%"${ident##*[![:space:]]}"}"
    if object_exists "$ident"; then
      SKIP_REASON="$ident already exists"
      return
    fi
  done
  for ident in "${present[@]+"${present[@]}"}"; do
    ident="${ident%"${ident##*[![:space:]]}"}"
    if ! object_exists "$ident"; then
      SKIP_REASON="$ident is absent"
      return
    fi
  done
  for other in "${skip_applied[@]+"${skip_applied[@]}"}"; do
    other="${other%"${other##*[![:space:]]}"}"
    if [[ -v "APPLIED[$other]" ]]; then
      SKIP_REASON="$other is already applied"
      return
    fi
  done

  SHOULD_APPLY=1
}

sql_guard() {
  local version="$1"
  local checksum="$2"
  local file="$3"
  local ident other line
  echo "DO \$guard\$"
  echo "DECLARE"
  echo "    existing text;"
  echo "BEGIN"
  echo "    SELECT checksum INTO existing FROM ops.schema_migrations WHERE version = '${version}';"
  echo "    IF existing IS NULL THEN"

  while IFS= read -r line || [[ -n "$line" ]]; do
    case "$line" in
      "-- migrate: apply-if-missing "*)
        ident="${line#-- migrate: apply-if-missing }"
        ident="${ident%"${ident##*[![:space:]]}"}"
        valid_ident "$ident"
        echo "        IF to_regclass('${ident}') IS NOT NULL THEN"
        echo "            RAISE EXCEPTION 'migration ${version} refused: ${ident} already exists';"
        echo "        END IF;"
        ;;
      "-- migrate: apply-if-present "*)
        ident="${line#-- migrate: apply-if-present }"
        ident="${ident%"${ident##*[![:space:]]}"}"
        valid_ident "$ident"
        echo "        IF to_regclass('${ident}') IS NULL THEN"
        echo "            RAISE EXCEPTION 'migration ${version} refused: ${ident} is absent';"
        echo "        END IF;"
        ;;
      "-- migrate: skip-if-applied "*)
        other="${line#-- migrate: skip-if-applied }"
        other="${other%"${other##*[![:space:]]}"}"
        valid_version "$other"
        echo "        IF EXISTS (SELECT 1 FROM ops.schema_migrations WHERE version = '${other}') THEN"
        echo "            RAISE EXCEPTION 'migration ${version} refused: ${other} is already applied';"
        echo "        END IF;"
        ;;
      *)
        ;;
    esac
  done < "$file"

  echo "    ELSIF existing <> '${checksum}' THEN"
  echo "        RAISE EXCEPTION 'migration ${version} checksum mismatch';"
  echo "    END IF;"
  echo "END"
  echo "\$guard\$;"
  echo "SELECT NOT EXISTS (SELECT 1 FROM ops.schema_migrations WHERE version = '${version}') AS should_apply \\gset"
  echo "\\if :should_apply"
  echo "\\echo applying ${version}"
  echo "\\ir ${version}"
  echo "INSERT INTO ops.schema_migrations (version, checksum) VALUES ('${version}', '${checksum}');"
  echo "\\else"
  echo "\\echo ${version} already applied"
  echo "\\endif"
}

append_secret_env() {
  local name="$1"
  local secret="$2"
  local delim
  delim="PW$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
  while [[ "$secret" == *"$delim"* ]]; do
    delim="PW$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
  done
  {
    printf '%s<<%s\n' "$name" "$delim"
    printf '%s\n' "$secret"
    printf '%s\n' "$delim"
  } >> "${GITHUB_ENV:?GITHUB_ENV is required}"
}

prepare_connection() {
  local tf_folder="${TF_FOLDER:?TF_FOLDER is required}"
  local infra_dir="${INFRA_DB_DIR:-infra/db}"
  local name desc arn raw password
  curl --proto '=https' --tlsv1.2 -fsSL -o /tmp/rds-global-bundle.pem https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem
  name=$(sed -n 's/^project_name *= *"\(.*\)".*/\1/p' "${infra_dir}/config/${tf_folder}/config.tfvars")
  if [[ -z "$name" ]]; then
    echo "project_name missing from ${infra_dir}/config/${tf_folder}/config.tfvars" >&2
    exit 1
  fi
  desc=$(aws rds describe-db-instances --db-instance-identifier "${name}-db" --query 'DBInstances[0].{Arn:MasterUserSecret.SecretArn,Host:Endpoint.Address,Port:Endpoint.Port,Db:DBName}' --output json)
  arn=$(printf '%s' "$desc" | jq -r .Arn)
  raw=$(aws secretsmanager get-secret-value --secret-id "$arn" --query SecretString --output text)
  echo "::add-mask::$raw"
  password=$(printf '%s' "$raw" | jq -r .password)
  echo "::add-mask::$password"
  {
    echo "PGHOST=$(printf '%s' "$desc" | jq -r .Host)"
    echo "PGPORT=$(printf '%s' "$desc" | jq -r .Port)"
    echo "PGDATABASE=$(printf '%s' "$desc" | jq -r .Db)"
    echo "PGUSER=$(printf '%s' "$raw" | jq -r .username)"
    echo "PGSSLMODE=verify-full"
    echo "PGSSLROOTCERT=/tmp/rds-global-bundle.pem"
  } >> "${GITHUB_ENV:?GITHUB_ENV is required}"
  append_secret_env PGPASSWORD "$password"
}

plan_migrations() {
  local out_dir="$1"
  local migrations_dir="$2"
  mkdir -p "$out_dir"
  rm -f "$out_dir"/*.sql "$out_dir"/manifest.tsv "$out_dir"/pending "$out_dir"/apply.sql
  : > "$out_dir/manifest.tsv"
  load_applied
  local transfer_pending=false
  if ownership_transfer_is_pending "$migrations_dir"; then
    transfer_pending=true
  fi
  report_public_owners "$transfer_pending"
  local pending=false
  shopt -s nullglob
  local -a files=("$migrations_dir"/*.sql)
  if [[ ${#files[@]} -eq 0 ]]; then
    echo "No migration files in $migrations_dir"
  fi
  local file version
  local IFS=$'\n'
  local -a files_sorted=($(printf '%s\n' "${files[@]+"${files[@]}"}" | LC_ALL=C sort))
  unset IFS
  for file in "${files_sorted[@]+"${files_sorted[@]}"}"; do
    version="$(basename "$file")"
    if ! valid_version "$version"; then
      echo "migration file name must be YYYYMMDD_NNN_description.sql: $version" >&2
      exit 1
    fi
    evaluate_file "$file"
    if [[ "$SHOULD_APPLY" == "1" ]]; then
      pending=true
      cp "$file" "$out_dir/$version"
      printf '%s\t%s\n' "$version" "$FILE_CHECKSUM" >> "$out_dir/manifest.tsv"
      echo "========== APPLY $version =========="
      cat "$file"
      echo "========== END $version =========="
    else
      echo "========== SKIP $version ($SKIP_REASON) =========="
    fi
  done
  if [[ "$pending" == "true" ]]; then
    echo "========== BOOKKEEPING =========="
    echo "CREATE SCHEMA IF NOT EXISTS ops;"
    echo "REVOKE ALL ON SCHEMA ops FROM PUBLIC;"
    echo "CREATE TABLE IF NOT EXISTS ops.schema_migrations ("
    echo "    version text PRIMARY KEY,"
    echo "    checksum text NOT NULL,"
    echo "    applied_at timestamptz NOT NULL DEFAULT now()"
    echo ");"
    echo "REVOKE ALL ON TABLE ops.schema_migrations FROM PUBLIC;"
    echo "========== END BOOKKEEPING =========="
  else
    echo "No pending migrations."
  fi
  echo "$pending" > "$out_dir/pending"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo "pending=$pending" >> "$GITHUB_OUTPUT"
  fi
}

apply_migrations() {
  local out_dir="$1"
  local pending_file="$out_dir/pending"
  if [[ -f "$pending_file" && "$(tr -d '[:space:]' < "$pending_file")" != "true" ]]; then
    echo "No pending migrations."
    return
  fi

  local manifest="$out_dir/manifest.tsv"
  if [[ ! -f "$manifest" ]]; then
    echo "missing manifest.tsv" >&2
    exit 1
  fi
  local transfer_pending=false
  local planned
  shopt -s nullglob
  for planned in "$out_dir"/*.sql; do
    if grep -q -- '^-- migrate: transfers-ownership-from marten_app$' "$planned"; then
      transfer_pending=true
    fi
  done
  report_public_owners "$transfer_pending"

  local apply_sql="$out_dir/apply.sql"
  {
    echo "BEGIN;"
    echo "SET LOCAL lock_timeout = '5s';"
    echo "SET LOCAL statement_timeout = '5min';"
    echo "SELECT pg_advisory_xact_lock(591022051);"
    echo "CREATE SCHEMA IF NOT EXISTS ops;"
    echo "REVOKE ALL ON SCHEMA ops FROM PUBLIC;"
    echo "CREATE TABLE IF NOT EXISTS ops.schema_migrations ("
    echo "    version text PRIMARY KEY,"
    echo "    checksum text NOT NULL,"
    echo "    applied_at timestamptz NOT NULL DEFAULT now()"
    echo ");"
    echo "REVOKE ALL ON TABLE ops.schema_migrations FROM PUBLIC;"
    local version checksum file actual
    while IFS=$'\t' read -r version checksum; do
      [[ -z "$version" ]] && continue
      valid_version "$version"
      [[ "$checksum" =~ ^[0-9a-f]{64}$ ]]
      file="$out_dir/$version"
      if [[ ! -f "$file" ]]; then
        echo "planned file missing: $version" >&2
        exit 1
      fi
      actual="$(sha256sum "$file" | awk '{print $1}')"
      if [[ "$actual" != "$checksum" ]]; then
        echo "planned checksum mismatch for $version" >&2
        exit 1
      fi
      sql_guard "$version" "$checksum" "$file"
    done < "$manifest"
    echo "COMMIT;"
  } > "$apply_sql"

  echo "========== APPLYING PLANNED MIGRATIONS =========="
  psql_at -f "$apply_sql"
  echo "Migrations applied."
}

main() {
  local command="${1:-}"
  local out_dir="${2:-}"
  local migrations_dir="${MIGRATIONS_DIR:-db/migrations}"
  export PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-15}"

  case "$command" in
    prepare)
      prepare_connection
      ;;
    plan)
      if [[ -z "$out_dir" ]]; then
        echo "missing output directory" >&2
        exit 2
      fi
      plan_migrations "$out_dir" "$migrations_dir"
      ;;
    apply)
      if [[ -z "$out_dir" ]]; then
        echo "missing output directory" >&2
        exit 2
      fi
      apply_migrations "$out_dir"
      ;;
    *)
      echo "usage: db-migrate.sh prepare | db-migrate.sh plan <dir> | db-migrate.sh apply <dir>" >&2
      exit 2
      ;;
  esac
}

main "$@"
