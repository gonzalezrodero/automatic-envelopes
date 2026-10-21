using System.Text.Json;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using AutomaticEnvelopes.Api.Features.Tenancy;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
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
    private const string Table = "public.mt_doc_tenantmonthusage";
    private readonly UsageOptions options = options.Value;

    public UsageMeter(IDocumentStore store)
        : this(store, Options.Create(new UsageOptions()), NullLogger<UsageMeter>.Instance)
    {
    }

    public async Task<int> ResponseCountAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var doc = await session.LoadAsync<TenantMonthUsage>(UsageMonth.Current(), ct);
        return doc?.Responses ?? 0;
    }

    public async Task RecordAsync(TenantProfile tenant, long inputTokens, long outputTokens, CancellationToken ct)
    {
        try
        {
            var limit = tenant.EffectiveMonthlyResponseLimit;
            var doc = await IncrementAsync(tenant.Id, inputTokens, outputTokens, ct);
            if (doc is null)
            {
                logger.LogError("Monthly usage row missing after increment. TenantId: {TenantId}.", tenant.Id);
                return;
            }

            var alert = UsageMath.Pending(doc, limit);
            if (alert != UsageAlert.None && await TrySendAsync(tenant, alert, doc.Responses, limit, ct))
                await MarkAsync(tenant.Id, alert == UsageAlert.Reached, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Monthly usage update failed. TenantId: {TenantId}.", tenant.Id);
        }
    }

    public async Task NotifyBlockedAsync(TenantProfile tenant, CancellationToken ct)
    {
        try
        {
            var limit = tenant.EffectiveMonthlyResponseLimit;
            await using var session = store.LightweightSession(tenant.Id);
            var doc = await session.LoadAsync<TenantMonthUsage>(UsageMonth.Current(), ct) ?? new TenantMonthUsage();
            if (doc.Notified100 || doc.Responses < limit)
                return;

            if (await TrySendAsync(tenant, UsageAlert.Reached, doc.Responses, limit, ct))
                await MarkAsync(tenant.Id, reached: true, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Monthly usage block notice failed. TenantId: {TenantId}.", tenant.Id);
        }
    }

    public async Task ClearAlertsAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        session.QueueSqlCommand(
            $$"""
            update {{Table}}
            set data = jsonb_set(jsonb_set(data, '{Notified80}', 'false'::jsonb, true), '{Notified100}', 'false'::jsonb, true),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            where tenant_id = ? and id = ?
            """,
            tenantId,
            UsageMonth.Current());
        await session.SaveChangesAsync(ct);
    }

    private async Task<TenantMonthUsage?> IncrementAsync(string tenantId, long inputTokens, long outputTokens, CancellationToken ct)
    {
        var month = UsageMonth.Current();
        var created = JsonSerializer.Serialize(new TenantMonthUsage
        {
            Id = month,
            Responses = 1,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        });
        await using var session = store.LightweightSession(tenantId);
        session.QueueSqlCommand(
            $$"""
            insert into {{Table}} (tenant_id, id, data)
            values (?, ?, cast(? as jsonb))
            on conflict (tenant_id, id) do update set
                data = jsonb_set(
                    jsonb_set(
                        jsonb_set({{Table}}.data, '{Responses}', to_jsonb(coalesce(({{Table}}.data->>'Responses')::int, 0) + 1)),
                        '{InputTokens}', to_jsonb(coalesce(({{Table}}.data->>'InputTokens')::bigint, 0) + cast(? as bigint))),
                    '{OutputTokens}', to_jsonb(coalesce(({{Table}}.data->>'OutputTokens')::bigint, 0) + cast(? as bigint))),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            """,
            tenantId,
            month,
            created,
            inputTokens,
            outputTokens);
        await session.SaveChangesAsync(ct);
        return await session.LoadAsync<TenantMonthUsage>(month, ct);
    }

    private async Task MarkAsync(string tenantId, bool reached, CancellationToken ct)
    {
        var month = UsageMonth.Current();
        var created = JsonSerializer.Serialize(new TenantMonthUsage
        {
            Id = month,
            Notified80 = true,
            Notified100 = reached
        });
        await using var session = store.LightweightSession(tenantId);
        session.QueueSqlCommand(
            $$"""
            insert into {{Table}} (tenant_id, id, data)
            values (?, ?, cast(? as jsonb))
            on conflict (tenant_id, id) do update set
                data = jsonb_set(
                    jsonb_set({{Table}}.data, '{Notified80}', 'true'::jsonb, true),
                    '{Notified100}', case when ? then 'true'::jsonb else coalesce({{Table}}.data->'Notified100', 'false'::jsonb) end, true),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            """,
            tenantId,
            month,
            created,
            reached);
        await session.SaveChangesAsync(ct);
    }

    private async Task<bool> TrySendAsync(TenantProfile tenant, UsageAlert alert, int responses, int limit, CancellationToken ct)
    {
        var to = tenant.ContactEmail?.Trim() ?? string.Empty;
        if (email is null || string.IsNullOrWhiteSpace(options.FromAddress) || string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Usage alert not emailed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
            return false;
        }

        var name = string.IsNullOrWhiteSpace(tenant.Name) ? tenant.Id : tenant.Name.Trim();
        name = name.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        var month = UsageMonth.Current();
        var reached = alert == UsageAlert.Reached;
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
                        Subject = new Content
                        {
                            Data = reached ? "Límite de respuestas alcanzado" : "Aviso de uso del asistente (80%)",
                            Charset = "UTF-8"
                        },
                        Body = new Body
                        {
                            Text = new Content
                            {
                                Data = reached
                                    ? $"{name} ha alcanzado el límite de {limit} respuestas de {month}. El asistente no responderá preguntas nuevas hasta el mes que viene. Para ampliar el límite, contacta con el administrador de la plataforma."
                                    : $"{name} lleva {responses} de {limit} respuestas de {month}. Al llegar al límite el asistente dejará de responder hasta el mes que viene.",
                                Charset = "UTF-8"
                            }
                        }
                    }
                }
            }, ct);
            logger.LogInformation("Usage alert emailed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Usage alert email failed. TenantId: {TenantId}. Alert: {Alert}.", tenant.Id, alert);
            return false;
        }
    }
}
