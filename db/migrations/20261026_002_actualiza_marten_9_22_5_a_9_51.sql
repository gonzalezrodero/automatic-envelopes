-- Marten 9.51.0 db-patch from the Marten 9.22.5 schema (Weasel WriteAllUpdates).
-- Generated from this application's Marten and Wolverine configuration against
-- PostgreSQL 16 with pgvector. Applying it leaves that database matching 9.51.0.
-- migrate: apply-if-present public.mt_streams
-- migrate: skip-if-applied 20261026_001_esquema_inicial_marten_9_51_wolverine_6_51.sql
--
-- Adds mt_streams.compacted_version and the mt_event_progression monitoring
-- columns, replaces mt_quick_append_events (same signature, CREATE OR REPLACE),
-- and adds the Wolverine 6.51.2 envelope indexes. Existing rows are kept.
-- The column changes and index builds take a short ACCESS EXCLUSIVE lock.

CREATE EXTENSION IF NOT EXISTS vector;

alter table public.mt_streams add column compacted_version bigint NOT NULL DEFAULT 0;
alter table public.mt_event_progression add column mode varchar NULL DEFAULT 'none';
alter table public.mt_event_progression add column rebuild_threshold integer NULL DEFAULT 0;
alter table public.mt_event_progression add column assigned_node integer NULL DEFAULT 0;
alter table public.mt_event_progression add column heartbeat timestamp with time zone NULL;
alter table public.mt_event_progression add column agent_status varchar(20) NULL;
alter table public.mt_event_progression add column pause_reason text NULL;
alter table public.mt_event_progression add column running_on_node integer NULL;
alter table public.mt_event_progression add column warning_behind_threshold bigint NULL;
alter table public.mt_event_progression add column critical_behind_threshold bigint NULL;
alter table public.mt_event_progression add column failure_category varchar(50) NULL;
alter table public.mt_event_progression add column failure_event_sequence bigint NULL;
alter table public.mt_event_progression add column failure_event_type varchar(500) NULL;
alter table public.mt_event_progression add column failure_event_tenant_id varchar(500) NULL;
CREATE OR REPLACE FUNCTION public.mt_quick_append_events(stream varchar, stream_type varchar, tenantid varchar, event_ids uuid[], event_types varchar[], dotnet_types varchar[], bodies jsonb[], bdatas bytea[], timestamps timestamp with time zone[], expected_version bigint DEFAULT NULL::bigint) RETURNS bigint[] AS $$
DECLARE
	event_version bigint;
	stream_is_archived boolean;
	event_type varchar;
	event_id uuid;
	body jsonb;
	index int;
	seq bigint;
    actual_tenant varchar;
    is_new_stream boolean := false;
	return_value bigint[];
BEGIN
    if expected_version IS NOT NULL then
        -- COALESCE turns the NULL we get for a brand-new stream into 0, so a
        -- FetchForWriting against a non-existent stream (which sets
        -- ExpectedVersionOnServer = 0) and a StartStream(id, version: 0) both
        -- land on the new-stream branch instead of mis-firing the guard.
        select version, is_archived into event_version, stream_is_archived from public.mt_streams where id = stream AND tenant_id = tenantid;
        if COALESCE(event_version, 0) != expected_version then
            RAISE EXCEPTION 'Stream version mismatch on ''%'': expected %, actual %', stream, expected_version, COALESCE(event_version, 0) USING ERRCODE = 'MT003';
        end if;
    else
        select version, is_archived into event_version, stream_is_archived from public.mt_streams where id = stream AND tenant_id = tenantid;
    end if;

	if event_version IS NULL then
		event_version = 0;
		is_new_stream := true;
		-- #5456 follow-up: insert the FINAL version straight away rather than 0 plus a
		-- trailing UPDATE of the row we just wrote in this same transaction. That UPDATE
		-- is a second heap tuple + WAL record per stream creation for no observable gain
		-- -- nothing outside this transaction can see the intermediate 0. The trailing
		-- UPDATE below is skipped for this branch via is_new_stream.
		insert into public.mt_streams (id, type, version, timestamp, tenant_id) values (stream, stream_type, COALESCE(array_length(event_ids, 1), 0), now(), tenantid);
    else
        if stream_is_archived then
            RAISE EXCEPTION 'Attempted to append event to archived stream with Id ''%''.', stream USING ERRCODE = 'MT001';
        end if;
        if tenantid IS NOT NULL then
            select tenant_id into actual_tenant from public.mt_streams where id = stream AND tenant_id = tenantid;
            if actual_tenant != tenantid then
                RAISE EXCEPTION 'The tenantid does not match the existing stream';
            end if;
        end if;
	end if;

	index := 1;
	-- #5062: array_length('{}', 1) is NULL in PostgreSQL, not 0, so a call with an
	-- empty event array used to return ARRAY[NULL] -- a bigint[] whose only element
	-- is NULL, which Npgsql cannot read into long[] ('Cannot read a non-nullable
	-- collection of elements because the returned array contains nulls'). COALESCE
	-- makes the empty case mean what it says: zero events appended, so the final
	-- version is the stream's current version.
	return_value := ARRAY[event_version + COALESCE(array_length(event_ids, 1), 0)];

	foreach event_id in ARRAY event_ids
	loop
        seq := nextval('public.mt_events_sequence');
		return_value := array_append(return_value, seq);

	    event_version := event_version + 1;
		event_type = event_types[index];
		body = bodies[index];

		-- #4515 / #4578 / Phase 2: bdatas[index] carries the binary payload
		-- for events opted in to binary serialization (NULL otherwise).
		-- bodies[index] is the {} JSON placeholder for those events so the
		-- existing data jsonb NOT NULL constraint stays intact.
		insert into public.mt_events
			(seq_id, id, stream_id, version, data, bdata, type, tenant_id, timestamp, mt_dotnet_type, is_archived)
		values
			(seq, event_id, stream, event_version, body, bdatas[index], event_type, tenantid, timestamps[index], dotnet_types[index], FALSE);

		index := index + 1;
	end loop;

	-- A brand-new stream already carries its final version from the insert above, so the
	-- UPDATE is only needed when we appended onto a stream that already existed.
	--
	-- #5539: and only when we actually appended something. With ZERO events this was a BLIND write-back
	-- of the version read at the top of the function, and under READ COMMITTED that is a lost update: the
	-- UPDATE waits on a concurrent appender's row lock, the winner commits, the WHERE clause is
	-- re-evaluated against the new row, and `set version = <the stale value>` lands -- leaving
	-- mt_streams.version BELOW the stream's own events. Every later append then computes a version that
	-- already exists and dies on pk_mt_events_stream_and_version, forever, until the row is repaired by
	-- hand. A non-empty call cannot do this: its first INSERT collides with the winner on that same
	-- primary key and rolls the whole thing back, which is why only the empty case was exposed.
	--
	-- Two independent guards, because either alone would have been enough and the cost of both is nil:
	--   * the append count -- appending nothing has nothing to write, so the correct number of statements
	--     is zero rather than one that happens to be harmless;
	--   * greatest(), so the write-back can never LOWER a stream's version whoever calls it. An append
	--     only ever moves a version forward, so this is a no-op on every correct path and a backstop on
	--     any future caller that reads the version and then writes it back.
	if not is_new_stream and COALESCE(array_length(event_ids, 1), 0) > 0 then
		update public.mt_streams set version = greatest(version, event_version), timestamp = now() where id = stream AND tenant_id = tenantid;
	end if;

	return return_value;
END
$$ LANGUAGE plpgsql;


-- Wolverine 6.51.2 indexes missing from the 6.25.3 message store.
-- The envelope tables already exist; these builds take a short lock.

CREATE INDEX IF NOT EXISTS idx_wolverine_outgoing_envelopes_owner ON public.wolverine_outgoing_envelopes USING btree (owner_id) WHERE (owner_id <> 0);
CREATE INDEX IF NOT EXISTS idx_wolverine_outgoing_envelopes_recover ON public.wolverine_outgoing_envelopes USING btree (destination) WHERE (owner_id = 0);
CREATE INDEX IF NOT EXISTS idx_wolverine_incoming_envelopes_owner ON public.wolverine_incoming_envelopes USING btree (owner_id) WHERE (owner_id <> 0);
CREATE INDEX IF NOT EXISTS idx_wolverine_incoming_envelopes_recover ON public.wolverine_incoming_envelopes USING btree (received_at) WHERE (status = 'Incoming' AND owner_id = 0);
CREATE INDEX IF NOT EXISTS idx_wolverine_incoming_envelopes_keep_until ON public.wolverine_incoming_envelopes USING btree (keep_until) WHERE (status = 'Handled');

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
