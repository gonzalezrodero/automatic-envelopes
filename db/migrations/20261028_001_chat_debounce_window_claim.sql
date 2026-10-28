-- Marten 9.51.0 db-patch for the chat debounce completion marker.
-- Generated with:
--   dotnet run --project src/AutomaticEnvelopes.Api -- db-patch db/migrations/20261028_001_chat_debounce_window_claim.sql
-- against a database that already had 20261026_001 applied.
--
-- Adds mt_doc_chatdebouncemarker. One row claims a closed debounce window
-- (id = saga id). A second row (id = recovery) is the sweep lease.
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

-- marten_app already has schema usage from 20261026_001. This table is new, so
-- grant DML on it alone. CONNECT, CREATE, and ALL TABLES stay in that earlier migration.
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.mt_doc_chatdebouncemarker TO marten_app;
