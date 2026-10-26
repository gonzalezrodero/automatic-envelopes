using AutomaticEnvelopes.Api;
using AwesomeAssertions;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AutomaticEnvelopes.Tests;

[Collection("Integration")]
public class DeployedDatabaseSchemaTests(IntegrationAppFixture fixture)
{
    [Fact]
    public void Lambda_turns_off_schema_changes_at_startup()
    {
        fixture.Host.Should().NotBeNull();
        var previous = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
        Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", "automatic-envelopes-api");
        try
        {
            var services = new ServiceCollection();
            services.AddDatabase("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");

            using var provider = services.BuildServiceProvider();
            var jasper = provider.GetRequiredService<JasperFxOptions>();
            jasper.Development.ResourceAutoCreate.Should().Be(AutoCreate.None);
            jasper.Production.ResourceAutoCreate.Should().Be(AutoCreate.None);

            var store = (DocumentStore)provider.GetRequiredService<IDocumentStore>();
            store.Options.AutoCreateSchemaObjects.Should().Be(AutoCreate.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", previous);
        }
    }

    [Fact]
    public async Task Baseline_script_matches_marten_configuration()
    {
        fixture.Host.Should().NotBeNull();
        const string database = "marten_baseline_check";
        var admin = Environment.GetEnvironmentVariable("ConnectionStrings__Marten")
            ?? throw new InvalidOperationException("Integration fixture did not set the Marten connection string.");

        var cancellation = TestContext.Current.CancellationToken;
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync(cancellation);
            await ExecuteAsync(connection, """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'marten_app') THEN
                        CREATE ROLE marten_app LOGIN PASSWORD 'marten_app_test' NOSUPERUSER NOCREATEDB NOCREATEROLE;
                    END IF;
                END
                $$;
                """, cancellation);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {database};", cancellation);
            await ExecuteAsync(connection, $"CREATE DATABASE {database};", cancellation);
        }

        var baseline = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        try
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "db/migrations/20261026_001_esquema_inicial_marten_9_51_wolverine_6_51.sql"), cancellation);
            await using (var connection = new NpgsqlConnection(baseline))
            {
                await connection.OpenAsync(cancellation);
                await ExecuteAsync(connection, sql, cancellation);
            }

            var previous = Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME");
            Environment.SetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME", "automatic-envelopes-api");
            try
            {
                var services = new ServiceCollection();
                services.AddDatabase(baseline);
                using var provider = services.BuildServiceProvider();
                var store = provider.GetRequiredService<IDocumentStore>();
                var act = async () => await store.Storage.Database.AssertDatabaseMatchesConfigurationAsync(cancellation);
                await act.Should().NotThrowAsync();
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellation)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(cancellation);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutomaticEnvelopes.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
