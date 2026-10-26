namespace AutomaticEnvelopes.Api;

/// <summary>
/// Deployed Lambdas are not the owner of the Marten tables. Schema changes are
/// versioned SQL applied as the RDS master user. The dev Lambda still uses the
/// Development environment, so the Lambda runtime name is the deployed signal.
/// </summary>
public static class DatabaseSchemaStartup
{
    public static bool AppliesAtStartup(string? lambdaFunctionName) =>
        string.IsNullOrEmpty(lambdaFunctionName);

    public static bool AppliesAtStartup() =>
        AppliesAtStartup(Environment.GetEnvironmentVariable("AWS_LAMBDA_FUNCTION_NAME"));
}
