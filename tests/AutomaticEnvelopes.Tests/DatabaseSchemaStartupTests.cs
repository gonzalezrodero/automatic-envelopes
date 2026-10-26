using AutomaticEnvelopes.Api;
using AwesomeAssertions;

namespace AutomaticEnvelopes.Tests;

public class DatabaseSchemaStartupTests
{
    [Fact]
    public void Local_and_tests_apply_schema_at_startup()
    {
        DatabaseSchemaStartup.AppliesAtStartup(null).Should().BeTrue();
        DatabaseSchemaStartup.AppliesAtStartup("").Should().BeTrue();
    }

    [Fact]
    public void Deployed_lambda_does_not_apply_schema_at_startup()
    {
        DatabaseSchemaStartup.AppliesAtStartup("automatic-envelopes-api").Should().BeFalse();
    }

    [Fact]
    public void Migration_scripts_grant_marten_app_and_are_ordered()
    {
        var root = RepoRoot();
        var upgrade = File.ReadAllText(Path.Combine(root, "db/migrations/20261026_002_actualiza_marten_9_22_5_a_9_51.sql"));
        var baseline = File.ReadAllText(Path.Combine(root, "db/migrations/20261026_001_esquema_inicial_marten_9_51_wolverine_6_51.sql"));

        upgrade.Should().Contain("alter table public.mt_streams add column compacted_version");
        upgrade.Should().Contain("CREATE OR REPLACE FUNCTION public.mt_quick_append_events");
        upgrade.Should().NotContain("DROP FUNCTION");
        upgrade.Should().NotContain("CASCADE");
        upgrade.Should().Contain("-- migrate: apply-if-present public.mt_streams");
        upgrade.Should().Contain("-- migrate: skip-if-applied 20261026_001_esquema_inicial_marten_9_51_wolverine_6_51.sql");
        baseline.Should().Contain("-- migrate: apply-if-missing public.mt_streams");
        baseline.Should().Contain("compacted_version");

        foreach (var script in new[] { upgrade, baseline })
        {
            script.Should().Contain("GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO marten_app");
            script.Should().Contain("GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO marten_app");
            script.Should().Contain("GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO marten_app");
        }
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
