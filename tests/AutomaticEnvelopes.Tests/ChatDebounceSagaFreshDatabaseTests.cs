using AutomaticEnvelopes.Api;
using AwesomeAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutomaticEnvelopes.Tests;

[Collection("Integration")]
public class ChatDebounceSagaFreshDatabaseTests(IntegrationAppFixture fixture)
{
    [Fact]
    public async Task Startup_apply_creates_the_single_tenant_chat_debounce_tables()
    {
        fixture.Host.Should().NotBeNull();
        const string database = "chat_debounce_saga_fresh";
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__Marten")
            ?? throw new InvalidOperationException("Integration fixture did not set the Marten connection string.");

        var cancellation = TestContext.Current.CancellationToken;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(cancellation);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database};", cancellation);
            await ExecuteAsync(connection, $"CREATE DATABASE {database};", cancellation);
        }

        var fresh = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        try
        {
            var previous = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", null);
            try
            {
                var services = new ServiceCollection();
                services.AddDatabase(fresh);
                await using var provider = services.BuildServiceProvider();
                var store = provider.GetRequiredService<IDocumentStore>();
                await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

                await using var connection = new NpgsqlConnection(fresh);
                await connection.OpenAsync(cancellation);
                await AssertDocumentTableAsync(
                    connection,
                    "mt_doc_chatdebouncesaga",
                    [
                        new CatalogColumn("id", "character varying", Nullable: false),
                        new CatalogColumn("data", "jsonb", Nullable: false),
                        new CatalogColumn("mt_last_modified", "timestamp with time zone", Nullable: true),
                        new CatalogColumn("mt_dotnet_type", "character varying", Nullable: true),
                        new CatalogColumn("mt_version", "integer", Nullable: false)
                    ],
                    cancellation);

                await AssertDocumentTableAsync(
                    connection,
                    "mt_doc_chatdebouncemarker",
                    [
                        new CatalogColumn("id", "character varying", Nullable: false),
                        new CatalogColumn("data", "jsonb", Nullable: false),
                        new CatalogColumn("mt_last_modified", "timestamp with time zone", Nullable: true),
                        new CatalogColumn("mt_version", "uuid", Nullable: false),
                        new CatalogColumn("mt_dotnet_type", "character varying", Nullable: true)
                    ],
                    cancellation);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", previous);
            }
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync(cancellation);
            await ExecuteAsync(connection, $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{database}' AND pid <> pg_backend_pid();
                """, cancellation);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database};", cancellation);
        }
    }

    private static async Task AssertDocumentTableAsync(
        NpgsqlConnection connection,
        string tableName,
        CatalogColumn[] columns,
        CancellationToken cancellation)
    {
        (await TableExistsAsync(connection, tableName, cancellation)).Should().BeTrue();

        var actual = await ReadColumnsAsync(connection, tableName, cancellation);
        actual.Should().BeEquivalentTo(columns);
        actual.Select(column => column.Name).Should().NotContain("tenant_id");

        (await ReadPrimaryKeyColumnsAsync(connection, tableName, cancellation)).Should().Equal("id");

        var indexes = await ReadIndexesAsync(connection, tableName, cancellation);
        indexes.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @table
                  AND table_type = 'BASE TABLE');
            """, connection);
        command.Parameters.AddWithValue("table", tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellation))!;
    }

    private static async Task<List<CatalogColumn>> ReadColumnsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand("""
            SELECT column_name,
                   CASE WHEN data_type = 'USER-DEFINED' THEN udt_name ELSE data_type END,
                   is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = @table
            ORDER BY ordinal_position;
            """, connection);
        command.Parameters.AddWithValue("table", tableName);
        var columns = new List<CatalogColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            columns.Add(new CatalogColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2) == "YES"));
        }

        return columns;
    }

    private static async Task<List<string>> ReadPrimaryKeyColumnsAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand("""
            SELECT attr.attname
            FROM pg_catalog.pg_index idx
            JOIN pg_catalog.pg_class tbl ON tbl.oid = idx.indrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = tbl.relnamespace
            JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS key(attnum, ordinality) ON true
            JOIN pg_catalog.pg_attribute attr
                ON attr.attrelid = tbl.oid AND attr.attnum = key.attnum
            WHERE ns.nspname = 'public'
              AND tbl.relname = @table
              AND idx.indisprimary
            ORDER BY key.ordinality;
            """, connection);
        command.Parameters.AddWithValue("table", tableName);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<List<CatalogIndex>> ReadIndexesAsync(NpgsqlConnection connection, string tableName, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand("""
            SELECT idx.indisprimary, index_rel.relname
            FROM pg_catalog.pg_index idx
            JOIN pg_catalog.pg_class tbl ON tbl.oid = idx.indrelid
            JOIN pg_catalog.pg_namespace ns ON ns.oid = tbl.relnamespace
            JOIN pg_catalog.pg_class index_rel ON index_rel.oid = idx.indexrelid
            WHERE ns.nspname = 'public'
              AND tbl.relname = @table
            ORDER BY index_rel.relname;
            """, connection);
        command.Parameters.AddWithValue("table", tableName);
        var indexes = new List<CatalogIndex>();
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        while (await reader.ReadAsync(cancellation))
        {
            indexes.Add(new CatalogIndex(reader.GetBoolean(0), reader.GetString(1)));
        }

        return indexes;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private sealed record CatalogColumn(string Name, string DataType, bool Nullable);

    private sealed record CatalogIndex(bool IsPrimary, string Name);
}
