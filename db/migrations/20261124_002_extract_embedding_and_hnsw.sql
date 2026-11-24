-- extract_embedding and the HNSW index used by knowledge search.
-- The HTTP Lambda used to create the function on startup. Deployed Lambdas
-- do not apply schema. The index matches HnswIndexCustomizer.WriteTemplate.
-- pgvector stores the function result as an unbounded vector, so the index
-- expression casts it to vector(512). Without that cast, CREATE INDEX fails
-- with "column does not have dimensions".
-- An existing function is left in place. The HTTP Lambda may already own it
-- as marten_app, and the master role cannot replace that function.

CREATE EXTENSION IF NOT EXISTS vector;

DO $do$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
          AND p.proname = 'extract_embedding'
          AND pg_get_function_identity_arguments(p.oid) = 'data jsonb'
    ) THEN
        EXECUTE $fn$
            CREATE FUNCTION public.extract_embedding(data jsonb)
            RETURNS vector(512)
            LANGUAGE plpgsql
            IMMUTABLE PARALLEL SAFE
            AS $body$
            BEGIN
                RETURN CAST(data ->> 'Embedding' AS vector(512));
            EXCEPTION WHEN OTHERS THEN
                RETURN array_fill(0, ARRAY[512])::vector;
            END;
            $body$
        $fn$;
    END IF;
END
$do$;

DO $grant$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'public'
          AND p.proname = 'extract_embedding'
          AND pg_get_function_identity_arguments(p.oid) = 'data jsonb'
          AND pg_get_userbyid(p.proowner) = current_user
    ) THEN
        GRANT EXECUTE ON FUNCTION public.extract_embedding(jsonb) TO marten_app;
    END IF;
END
$grant$;

CREATE INDEX IF NOT EXISTS mt_doc_documentchunk_idx_embedding
    ON public.mt_doc_documentchunk USING hnsw ((public.extract_embedding(data)::vector(512)) vector_cosine_ops);
