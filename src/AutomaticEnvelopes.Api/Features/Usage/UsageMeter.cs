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
    private const string Stamp = "mt_version = md5(random()::text || clock_timestamp()::text)::uuid, mt_last_modified = transaction_timestamp()";
    private readonly UsageOptions options = options.Value;

    public UsageMeter(IDocumentStore store)
        : this(store, Options.Create(new UsageOptions()), NullLogger<UsageMeter>.Instance) { }

    public async Task<int> ResponseCountAsync(string tenantId, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        return (await session.LoadAsync<TenantMonthUsage>(UsageMonth.Current(), ct))?.Responses ?? 0;
    }

    public Task RecordAsync(TenantProfile tenant, long inputTokens, long outputTokens, CancellationToken ct) =>
        Guard(tenant.Id, async () =>
        {
            var limit = tenant.EffectiveMonthlyResponseLimit;
            var doc = await IncrementAsync(tenant.Id, inputTokens, outputTokens, ct);
            if (doc is null)
            {
                logger.LogError("Monthly usage row missing after increment. TenantId: {TenantId}.", tenant.Id);
                return;
            }

            var reached = !doc.Notified100 && doc.Responses >= Threshold(limit, 100);
            var warning = !reached && !doc.Notified80 && doc.Responses >= Threshold(limit, 80);
            if ((reached || warning) && await TrySendAsync(tenant, reached, doc.Responses, limit, ct))
                await MarkAsync(tenant.Id, reached, ct);
        });

    public Task NotifyBlockedAsync(TenantProfile tenant, CancellationToken ct) =>
        Guard(tenant.Id, async () =>
        {
            var limit = tenant.EffectiveMonthlyResponseLimit;
            await using var session = store.LightweightSession(tenant.Id);
            var doc = await session.LoadAsync<TenantMonthUsage>(UsageMonth.Current(), ct) ?? new TenantMonthUsage();
            if (!doc.Notified100 && doc.Responses >= limit && await TrySendAsync(tenant, reached: true, doc.Responses, limit, ct))
                await MarkAsync(tenant.Id, reached: true, ct);
        });

    public Task ClearAlertsAsync(string tenantId, CancellationToken ct) =>
        Exec(tenantId, $$"""
            update {{Table}} set data = jsonb_set(jsonb_set(data, '{Notified80}', 'false'::jsonb, true), '{Notified100}', 'false'::jsonb, true), {{Stamp}}
            where tenant_id = ? and id = ?
            """, [tenantId, UsageMonth.Current()], ct);

    private async Task Guard(string tenantId, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Monthly usage update failed. TenantId: {TenantId}.", tenantId);
        }
    }

    private async Task<TenantMonthUsage?> IncrementAsync(string tenantId, long inputTokens, long outputTokens, CancellationToken ct)
    {
        var month = UsageMonth.Current();
        var created = JsonSerializer.Serialize(new TenantMonthUsage { Id = month, Responses = 1, InputTokens = inputTokens, OutputTokens = outputTokens });
        await Exec(tenantId, $$"""
            insert into {{Table}} (tenant_id, id, data) values (?, ?, cast(? as jsonb))
            on conflict (tenant_id, id) do update set data = jsonb_set(jsonb_set(jsonb_set({{Table}}.data,
                '{Responses}', to_jsonb(coalesce(({{Table}}.data->>'Responses')::int, 0) + 1)),
                '{InputTokens}', to_jsonb(coalesce(({{Table}}.data->>'InputTokens')::bigint, 0) + cast(? as bigint))),
                '{OutputTokens}', to_jsonb(coalesce(({{Table}}.data->>'OutputTokens')::bigint, 0) + cast(? as bigint))), {{Stamp}}
            """, [tenantId, month, created, inputTokens, outputTokens], ct);
        await using var session = store.LightweightSession(tenantId);
        return await session.LoadAsync<TenantMonthUsage>(month, ct);
    }

    private Task MarkAsync(string tenantId, bool reached, CancellationToken ct)
    {
        var month = UsageMonth.Current();
        var created = JsonSerializer.Serialize(new TenantMonthUsage { Id = month, Notified80 = true, Notified100 = reached });
        return Exec(tenantId, $$"""
            insert into {{Table}} (tenant_id, id, data) values (?, ?, cast(? as jsonb))
            on conflict (tenant_id, id) do update set data = jsonb_set(jsonb_set({{Table}}.data, '{Notified80}', 'true'::jsonb, true),
                '{Notified100}', case when ? then 'true'::jsonb else coalesce({{Table}}.data->'Notified100', 'false'::jsonb) end, true), {{Stamp}}
            """, [tenantId, month, created, reached], ct);
    }

    private async Task Exec(string tenantId, string sql, object[] args, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        session.QueueSqlCommand(sql, args);
        await session.SaveChangesAsync(ct);
    }

    private static int Threshold(int limit, int percent)
    {
        if (limit <= 0)
            return percent >= 100 ? 0 : int.MaxValue;

        return (int)((percent * (long)limit + 99) / 100);
    }

    private async Task<bool> TrySendAsync(TenantProfile tenant, bool reached, int responses, int limit, CancellationToken ct)
    {
        var to = tenant.ContactEmail?.Trim() ?? string.Empty;
        if (email is null || string.IsNullOrWhiteSpace(options.FromAddress) || string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Usage alert not emailed. TenantId: {TenantId}. Reached: {Reached}.", tenant.Id, reached);
            return false;
        }

        var name = UsageMessages.EmailName(tenant.Name);
        var month = UsageMonth.Spoken(DateTimeOffset.UtcNow);
        var subject = reached
            ? "El asistente ya no responderá hasta el mes que viene"
            : "El asistente está cerca del límite de respuestas";
        var body = reached
            ? $"{name} ya ha usado las {limit} respuestas de {month}. El asistente no contestará preguntas nuevas hasta el mes que viene. Si necesitas más, contacta con quien administra el servicio."
            : $"{name} lleva {responses} de {limit} respuestas en {month}. Cuando se acaben, el asistente dejará de contestar hasta el mes que viene.";
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
                        Subject = new Content { Data = subject, Charset = "UTF-8" },
                        Body = new Body { Text = new Content { Data = body, Charset = "UTF-8" } }
                    }
                }
            }, ct);
            logger.LogInformation("Usage alert emailed. TenantId: {TenantId}. Reached: {Reached}.", tenant.Id, reached);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Usage alert email failed. TenantId: {TenantId}. Reached: {Reached}.", tenant.Id, reached);
            return false;
        }
    }
}
