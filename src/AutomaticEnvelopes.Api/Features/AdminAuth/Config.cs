using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AutomaticEnvelopes.Api.Features.AdminAuth;

public static class Config
{
    public static IServiceCollection AddAdminPortalAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var authOptions = AdminAuthOptions.FromConfiguration(configuration);
        services.AddSingleton<IOptions<AdminAuthOptions>>(Options.Create(authOptions));

        services.AddCors(cors =>
        {
            cors.AddPolicy(AdminPortalCors.PolicyName, policy =>
            {
                policy.WithOrigins(authOptions.CorsOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                    .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
            });
        });

        var poolConfigured = !string.IsNullOrWhiteSpace(authOptions.UserPoolId);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                if (poolConfigured)
                {
                    jwt.Authority = authOptions.Issuer;
                }

                jwt.SaveToken = true;
                jwt.MapInboundClaims = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = poolConfigured,
                    ValidIssuer = authOptions.Issuer,
                    ValidateLifetime = true,
                    ValidateAudience = true,
                    AudienceValidator = (audiences, securityToken, _) =>
                        CognitoAudienceValidator.ValidateAccessToken(audiences, securityToken, authOptions.ClientId),
                    RoleClaimType = "cognito:groups"
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        AdminAuthCookies.ReadAccessToken(context);
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddHttpClient(CognitoHttpClient.Name, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });

        services.AddSingleton<ICognitoSigningKeys, CognitoJwksProvider>();
        services.AddSingleton<ICognitoTokenClient, CognitoTokenClient>();
        services.AddSingleton<ICognitoIdTokenReader, CognitoIdTokenReader>();

        return services;
    }
}
