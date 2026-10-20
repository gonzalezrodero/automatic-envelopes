using Npgsql;

namespace AutomaticEnvelopes.Api.Features.Knowledge;

public static class VectorSchema
{
    public const string ExtractEmbeddingFunctionSql = """
        CREATE OR REPLACE FUNCTION public.extract_embedding(data jsonb)
        RETURNS vector IMMUTABLE PARALLEL SAFE AS $$
        BEGIN
            -- Ensure 'Embedding' matches your C# property name exactly
            RETURN CAST(data ->> 'Embedding' AS vector(512));
        EXCEPTION WHEN OTHERS THEN
            -- Fallback to a zero vector to avoid crashing the index
            RETURN array_fill(0, ARRAY[512])::vector;
        END;
        $$ LANGUAGE plpgsql;
        """;

    public static void Ensure(NpgsqlConnection connection)
    {
        if (!IsVectorInstalled(connection))
        {
            InstallVector(connection);
        }

        if (ExtractEmbeddingExists(connection))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = ExtractEmbeddingFunctionSql;
        command.ExecuteNonQuery();
    }

    private static bool IsVectorInstalled(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector');";
        return (bool)command.ExecuteScalar()!;
    }

    private static bool ExtractEmbeddingExists(NpgsqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public' AND p.proname = 'extract_embedding');
            """;
        return (bool)command.ExecuteScalar()!;
    }

    private static void InstallVector(NpgsqlConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
            command.ExecuteNonQuery();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                "pgvector is not installed, and this database login cannot create extensions.",
                ex);
        }
    }
}
