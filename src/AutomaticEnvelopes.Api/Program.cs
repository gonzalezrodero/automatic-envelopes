using AutomaticEnvelopes.Api;
using AutomaticEnvelopes.Api.Common.Extensions;
using JasperFx;
using Marten;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

// Logs
builder.Logging.AddLogging();

// AWS Config
builder.AddAwsSecureConfiguration();
var connectionString = builder.Configuration.GetConnectionString("Marten")!;

// Services
builder.Services.AddDatabase(connectionString);
builder.Services.AddAi(builder.Configuration);
builder.Services.AddFeatures(builder.Configuration);

// Wolverine (ahora desde builder.Services)
builder.Services.AddWolverine(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.AddWolverineHttp();
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

var app = builder.Build();

if (!args.Contains("codegen"))
{
    using (var scope = app.Services.CreateScope())
    {
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }
    app.EnsureVectorExtensionExists(connectionString);
}

app.MapWolverineEndpoints();
return await app.RunJasperFxCommands(args);