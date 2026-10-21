using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Npgsql;
using System.Text.Json;

namespace AutomaticEnvelopes.Api.Common.Extensions;

public static class AwsConfigurationExtensions
{
    public static void AddAwsSecureConfiguration(this IHostApplicationBuilder builder)
    {
        var martenArn = Environment.GetEnvironmentVariable("SECRET_ARN_MARTEN");
        var ssmPath = Environment.GetEnvironmentVariable("SSM_PATH_WHATSAPP");
        var usageArn = Environment.GetEnvironmentVariable("SECRET_ARN_USAGE");

        if (string.IsNullOrEmpty(martenArn) && string.IsNullOrEmpty(ssmPath) && string.IsNullOrEmpty(usageArn))
        {
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("codegen"))
        {
            return;
        }

        using var secretsClient = new AmazonSecretsManagerClient();
        using var ssmClient = new AmazonSimpleSystemsManagementClient();

        builder.AddAwsSecureConfigurationCore(secretsClient, ssmClient);
    }

    public static void AddAwsSecureConfigurationCore(
        this IHostApplicationBuilder builder,
        IAmazonSecretsManager secretsClient,
        IAmazonSimpleSystemsManagement ssmClient)
    {
        var secureConfig = new Dictionary<string, string?>();

        // 1. Fetch the Marten login from Secrets Manager. Deployed Lambdas use the
        //    marten_app secret, not the RDS master user.
        var martenArn = Environment.GetEnvironmentVariable("SECRET_ARN_MARTEN");
        var dbEndpoint = Environment.GetEnvironmentVariable("DB_HOST");

        if (!string.IsNullOrEmpty(martenArn) && !string.IsNullOrEmpty(dbEndpoint))
        {
            var response = secretsClient.GetSecretValueAsync(new GetSecretValueRequest { SecretId = martenArn })
                                        .GetAwaiter()
                                        .GetResult();

            var secretJson = response.SecretString;
            using var document = JsonDocument.Parse(secretJson);
            var root = document.RootElement;

            var hostParts = dbEndpoint.Split(':');
            var dbHost = hostParts[0];
            var dbPort = int.Parse(hostParts[1]);

            var connStringBuilder = new NpgsqlConnectionStringBuilder
            {
                Host = dbHost,
                Port = dbPort,
                Database = "automaticenvelopes",
                Username = root.GetProperty("username").GetString(),
                Password = root.GetProperty("password").GetString(),
                SslMode = SslMode.Require
            };

            secureConfig["ConnectionStrings:Marten"] = connStringBuilder.ToString();
        }

        // 2. Fetch WhatsApp tokens from SSM Parameter Store
        var ssmPath = Environment.GetEnvironmentVariable("SSM_PATH_WHATSAPP");
        if (!string.IsNullOrEmpty(ssmPath))
        {
            var response = ssmClient.GetParametersByPathAsync(new GetParametersByPathRequest
            {
                Path = ssmPath,
                WithDecryption = true
            }).GetAwaiter().GetResult();

            foreach (var param in response.Parameters)
            {
                // Map AWS path to .NET IOptions structure 
                // e.g., "/automatic-envelopes/whatsapp/app-secret" -> "WhatsApp:AppSecret"
                var keyName = param.Name.Split('/')[^1];
                var pascalCaseKey = string.Join("", keyName.Split('-').Select(p => char.ToUpper(p[0]) + p[1..]));

                secureConfig[$"WhatsApp:{pascalCaseKey}"] = param.Value;
            }
        }

        var usageArn = Environment.GetEnvironmentVariable("SECRET_ARN_USAGE");
        if (!string.IsNullOrEmpty(usageArn))
        {
            var response = secretsClient.GetSecretValueAsync(new GetSecretValueRequest { SecretId = usageArn })
                .GetAwaiter()
                .GetResult();
            if (!string.IsNullOrEmpty(response.SecretString))
                secureConfig["Usage:PhoneHashKey"] = response.SecretString;
        }

        // 3. Inject fetched secrets into .NET configuration
        if (secureConfig.Count > 0)
        {
            builder.Configuration.AddInMemoryCollection(secureConfig);
        }
    }
}