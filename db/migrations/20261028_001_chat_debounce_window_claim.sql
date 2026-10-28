-- Marten 9.51.0 db-patch for the chat debounce completion marker.
-- Generated with:
--   dotnet run --project src/AutomaticEnvelopes.Api -- db-patch db/migrations/20261028_001_chat_debounce_window_claim.sql
-- against a database that already had 20261026_001 applied.
--
-- Adds mt_doc_chatdebouncemarker. One row claims a closed debounce window
-- (id = saga id) and one lease row keeps the recovery chain alive.
-- ChatDebounceSaga.StartedAt stays inside the document JSON, so
-- mt_doc_chatdebouncesaga is unchanged.
-- CREATE TABLE IF NOT EXISTS is safe where the table is already present.

CREATE TABLE IF NOT EXISTS public.mt_doc_chatdebouncemarker (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_version          uuid                        NOT NULL DEFAULT (md5(random()::text || clock_timestamp()::text)::uuid),
    mt_dotnet_type      varchar                     NULL,
CONSTRAINT pkey_mt_doc_chatdebouncemarker_id PRIMARY KEY (id)
);

-- Grants for the Lambda login. marten_app is not the table owner (infra/db/marten_app.tf).
DO $marten_app_grants$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO marten_app', current_database());
    GRANT USAGE, CREATE ON SCHEMA public TO marten_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO marten_app;
    GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO marten_app;
    GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO marten_app;
END
$marten_app_grants$;
