using AutomaticEnvelopes.Api.Features.Knowledge;
using AwesomeAssertions;
using Npgsql;

namespace AutomaticEnvelopes.Tests.Features.Knowledge;

public class VectorSchemaSqlTests
{
    [Fact]
    public void ExtractEmbeddingFunction_StaysInvokerAndDoesNotInstallExtensions()
    {
        VectorSchema.ExtractEmbeddingFunctionSql.Should().Contain("extract_embedding");
        VectorSchema.ExtractEmbeddingFunctionSql.Should().Contain("vector(512)");
        VectorSchema.ExtractEmbeddingFunctionSql.Should().NotContain("SECURITY DEFINER");
        VectorSchema.ExtractEmbeddingFunctionSql.Should().NotContain("CREATE EXTENSION");
    }
}

[Collection("Integration")]
public class VectorSchemaPrivilegeTests(IntegrationAppFixture fixture)
{
    private const string Login = "vector_schema_test";
    private const string Password = "vector_schema_test_pw";
    private const string EmptyDatabase = "vector_schema_priv";

    [Fact]
    public void Ensure_WhenFunctionAlreadyExists_DoesNotRequireOwnership()
    {
        fixture.Host.Should().NotBeNull();
        var admin = AdminConnectionString();

        using (var connection = new NpgsqlConnection(admin))
        {
            connection.Open();
            EnsureLogin(connection);
            var database = new NpgsqlConnectionStringBuilder(admin).Database;
            Execute(connection, $"GRANT CONNECT ON DATABASE {database} TO {Login};");
        }

        using var app = new NpgsqlConnection(AppConnectionString(admin, new NpgsqlConnectionStringBuilder(admin).Database!));
        app.Open();
        var act = () => VectorSchema.Ensure(app);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ensure_WhenVectorIsMissing_AndLoginCannotCreateExtensions_Throws()
    {
        fixture.Host.Should().NotBeNull();
        var admin = AdminConnectionString();

        using (var connection = new NpgsqlConnection(admin))
        {
            connection.Open();
            EnsureLogin(connection);
            Execute(connection, $"DROP DATABASE IF EXISTS {EmptyDatabase};");
            Execute(connection, $"CREATE DATABASE {EmptyDatabase};");
        }

        try
        {
            using var app = new NpgsqlConnection(AppConnectionString(admin, EmptyDatabase));
            app.Open();
            var act = () => VectorSchema.Ensure(app);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*cannot create extensions*");
        }
        finally
        {
            using var connection = new NpgsqlConnection(admin);
            connection.Open();
            Execute(connection, $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{EmptyDatabase}' AND pid <> pg_backend_pid();
                """);
            Execute(connection, $"DROP DATABASE IF EXISTS {EmptyDatabase};");
        }
    }

    private static string AdminConnectionString()
    {
        return Environment.GetEnvironmentVariable("ConnectionStrings__Marten")
            ?? throw new InvalidOperationException("Integration fixture did not set the Marten connection string.");
    }

    private static string AppConnectionString(string admin, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = database,
            Username = Login,
            Password = Password
        };
        return builder.ConnectionString;
    }

    private static void EnsureLogin(NpgsqlConnection connection)
    {
        Execute(connection, $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Login}') THEN
                    CREATE ROLE {Login} LOGIN PASSWORD '{Password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
                END IF;
            END
            $$;
            """);
    }

    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
