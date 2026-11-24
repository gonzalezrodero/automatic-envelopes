-- mt_doc_chatdebouncesaga is the Wolverine debounce saga.
-- Generated from Marten 9.51 against a database that already had
-- 20261121_001 applied: register the document, let startup create the
-- table, then pg_dump --schema-only. db-patch reported no remaining diff
-- after that create, so it did not write this file itself.
--
-- mt_version is integer, which ChatWindowExpiredHandler reads as a number.
-- The table is single-tenanted. The id is tenantId:phone.
-- CREATE TABLE IF NOT EXISTS is safe where Marten already created it.

CREATE TABLE IF NOT EXISTS public.mt_doc_chatdebouncesaga (
    id                  varchar                     NOT NULL,
    data                jsonb                       NOT NULL,
    mt_last_modified    timestamp with time zone    NULL DEFAULT (transaction_timestamp()),
    mt_dotnet_type      varchar                     NULL,
    mt_version          integer                     NOT NULL DEFAULT 0,
    CONSTRAINT pkey_mt_doc_chatdebouncesaga_id PRIMARY KEY (id)
);

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.mt_doc_chatdebouncesaga TO marten_app;
