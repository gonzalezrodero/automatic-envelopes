using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using AutomaticEnvelopes.Api.Features.Tenancy;
using JasperFx;
using Marten;
using Microsoft.Extensions.Options;

namespace AutomaticEnvelopes.Api.Features.Usage;

public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    public string FromAddress { get; set; } = string.Empty;

    public string PhoneHashKey { get; set; } = string.Empty;
}

public sealed class UsageMeter(
    IDocumentStore store,
    IOptions<UsageOptions> options,
    ILogger<UsageMeter> logger,
    IAmazonSimpleEmailServiceV2? email = null)
{
    private const int Attempts = 5;
    private readonly UsageOptions options = options.Value;

    public async Task<int> ResponseCountAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var doc = await session.LoadAsync<TenantMonthUsage>(UsageMonth.Current(), ct);
        return doc?.Responses ?? 0;
    }

    public async Task RecordAsync(TenantProfile tenant, long inputTokens, long outputTokens, CancellationToken ct)
    {
        var limit = tenant.EffectiveMonthlyResponseLimit;
        var saved = await UpdateAsync(
            tenant.Id,
            doc => UsageMath.Record(doc, inputTokens, outputTokens, limit),
            ct);
        if (saved.Alert != UsageAlert.None)
        {
            await SendAsync(tenant, saved.Alert, saved.Responses, limit, ct);
        }
    }

    public async Task NotifyBlockedAsync(TenantProfile tenant, CancellationToken ct)
    {
        var limit = tenant.EffectiveMonthlyResponseLimit;
        var saved = await UpdateAsync(tenant.Id, doc => UsageMath.MarkBlocked(doc, limit), ct);
        if (saved.Alert == UsageAlert.Reached)
        {
            await SendAsync(tenant, saved.Alert, saved.Responses, limit, ct);
        }
    }

    private async Task<UsageUpdate> UpdateAsync(
        string tenantId,
        Func<TenantMonthUsage, UsageUpdate> change,
        CancellationToken ct)
    {
        var id = UsageMonth.Current();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            await using var session = store.LightweightSession(tenantId);
            var doc = await session.LoadAsync<TenantMonthUsage>(id, ct) ?? new TenantMonthUsage { Id = id };
            var update = change(doc);
            if (!update.Save)
            {
                return update;
            }

            session.Store(doc);
            try
            {
                await session.SaveChangesAsync(ct);
                return update;
            }
            catch (ConcurrencyException) when (attempt < Attempts)
            {
                logger.LogInformation("Retrying monthly usage update. TenantId: {TenantId}.", tenantId);
            }
        }

        logger.LogError("Monthly usage update failed after retries. TenantId: {TenantId}.", tenantId);
        return new UsageUpdate(false, UsageAlert.None, 0);
    }

    private async Task SendAsync(TenantProfile tenant, UsageAlert alert, int responses, int limit, CancellationToken ct)
    {
        var to = tenant.ContactEmail?.Trim() ?? string.Empty;
        if (email is null || string.IsNullOrWhiteSpace(options.FromAddress) || string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Usage alert not emailed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
            return;
        }

        try
        {
            await email.SendEmailAsync(new SendEmailRequest
            {
                FromEmailAddress = options.FromAddress,
                Destination = new Destination { ToAddresses = [to] },
                Content = new EmailContent
                {
                    Simple = new Message
                    {
                        Subject = new Content { Data = UsageMail.Subject(alert), Charset = "UTF-8" },
                        Body = new Body
                        {
                            Text = new Content
                            {
                                Data = UsageMail.Body(DisplayName(tenant), UsageMonth.Current(), responses, limit, alert),
                                Charset = "UTF-8"
                            }
                        }
                    }
                }
            }, ct);
            logger.LogInformation("Usage alert emailed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Usage alert email failed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
        }
    }

    private static string DisplayName(TenantProfile tenant)
    {
        var name = string.IsNullOrWhiteSpace(tenant.Name) ? tenant.Id : tenant.Name.Trim();
        return name.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }
}
