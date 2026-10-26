#!/usr/bin/env bash
# Apply db/migrations as the RDS master user. Plan prints the SQL. Apply runs
# the planned files once, recorded in ops.schema_migrations (not granted to marten_app).
set -euo pipefail

migrations_dir="${MIGRATIONS_DIR:-db/migrations}"
command="${1:-}"
out_dir="${2:-}"

if [[ "$command" != "plan" && "$command" != "apply" ]]; then
  echo "usage: db-migrate.sh plan <dir> | db-migrate.sh apply <dir>" >&2
  exit 2
fi

if [[ -z "$out_dir" ]]; then
  echo "missing output directory" >&2
  exit 2
fi

export PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-15}"

psql_at() {
  psql -v ON_ERROR_STOP=1 -X --no-psqlrc -q "$@"
}

psql_scalar() {
  psql_at -tA -c "$1"
}

valid_ident() {
  [[ "$1" =~ ^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$ ]]
}

valid_version() {
  [[ "$1" =~ ^[0-9]{3}_[a-z0-9_]+\.sql$ ]]
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

if [[ "$command" == "plan" ]]; then
  mkdir -p "$out_dir"
  rm -f "$out_dir"/*.sql "$out_dir"/manifest.tsv "$out_dir"/pending "$out_dir"/apply.sql
  : > "$out_dir/manifest.tsv"
  load_applied
  pending=false
  shopt -s nullglob
  files=("$migrations_dir"/*.sql)
  if [[ ${#files[@]} -eq 0 ]]; then
    echo "No migration files in $migrations_dir"
  fi
  IFS=$'\n' files_sorted=($(printf '%s\n' "${files[@]+"${files[@]}"}" | LC_ALL=C sort))
  unset IFS
  for file in "${files_sorted[@]+"${files_sorted[@]}"}"; do
    version="$(basename "$file")"
    if ! valid_version "$version"; then
      echo "migration file name must be NNN_description.sql: $version" >&2
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
  exit 0
fi

pending_file="$out_dir/pending"
if [[ -f "$pending_file" && "$(tr -d '[:space:]' < "$pending_file")" != "true" ]]; then
  echo "No pending migrations."
  exit 0
fi

manifest="$out_dir/manifest.tsv"
if [[ ! -f "$manifest" ]]; then
  echo "missing manifest.tsv" >&2
  exit 1
fi

apply_sql="$out_dir/apply.sql"
{
  echo "BEGIN;"
  echo "SELECT pg_advisory_xact_lock(591022051);"
  echo "CREATE SCHEMA IF NOT EXISTS ops;"
  echo "REVOKE ALL ON SCHEMA ops FROM PUBLIC;"
  echo "CREATE TABLE IF NOT EXISTS ops.schema_migrations ("
  echo "    version text PRIMARY KEY,"
  echo "    checksum text NOT NULL,"
  echo "    applied_at timestamptz NOT NULL DEFAULT now()"
  echo ");"
  echo "REVOKE ALL ON TABLE ops.schema_migrations FROM PUBLIC;"
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
