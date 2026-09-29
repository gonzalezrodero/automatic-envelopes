namespace AutomaticEnvelopes.Tests.Features.AdminAuth;

internal static class AdminAuthAppSettings
{
    public static string Path()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "src", "AutomaticEnvelopes.Api", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("appsettings.json was not found from the test output directory.");
    }
}
