using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cadence.Web.Data;

namespace Cadence.Web.Services;

/// <summary>
/// Outbound notifications to one URL chosen in Settings. Slack and Microsoft Teams incoming-webhook URLs get a
/// chat-friendly <c>{"text": ...}</c> body; any other URL gets a JSON event signed with the webhook secret, so a
/// receiving system can verify it the same way Cadence verifies inbound deliveries.
/// </summary>
public sealed class NotificationService(SettingsService settings, BackgroundJobs jobs)
{
    public async Task NotifyAsync(string eventName, string text, object data)
    {
        var ws = await settings.GetAsync();
        if (string.IsNullOrWhiteSpace(ws.OutboundUrl) || !ws.OutboundEvents.Contains(eventName)) return;
        var url = ws.OutboundUrl;
        jobs.Enqueue($"notify:{eventName}", (sp, ct) => sp.GetRequiredService<OutboundSender>().SendAsync(url, eventName, text, data, ct));
    }

    /// <summary>Sends immediately and reports the result (used by the "Send test" button and "Send report").</summary>
    public async Task<(bool Ok, string Message)> SendNowAsync(OutboundSender sender, string eventName, string text, object data, CancellationToken ct = default)
    {
        var ws = await settings.GetAsync();
        if (string.IsNullOrWhiteSpace(ws.OutboundUrl)) return (false, "Add a notification URL in Settings first.");
        return await sender.SendAsync(ws.OutboundUrl, eventName, text, data, ct);
    }
}

public sealed class OutboundSender(HttpClient http, WebhookRepository log, IConfiguration config, ILogger<OutboundSender> logger)
{
    public static bool IsChatWebhook(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith("hooks.slack.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("webhook.office.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase));

    public async Task<(bool Ok, string Message)> SendAsync(string url, string eventName, string text, object data, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return (false, "The notification URL must be an https:// address.");

        string body;
        if (IsChatWebhook(url))
            body = uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Serialize(new { content = text })
                : JsonSerializer.Serialize(new { text });
        else
            body = JsonSerializer.Serialize(new { @event = eventName, text, data, sentAt = DateTimeOffset.UtcNow }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var secret = config["Webhooks:Secret"];
        if (!IsChatWebhook(url) && !string.IsNullOrEmpty(secret))
            request.Headers.Add("X-Cadence-Signature", WebhookSignature.Compute(secret, Encoding.UTF8.GetBytes(body)));

        try
        {
            using var response = await http.SendAsync(request, ct);
            var ok = response.IsSuccessStatusCode;
            await log.LogAsync(uri.Host, eventName, body, true, ok ? "accepted" : "error", $"HTTP {(int)response.StatusCode}", null, "outbound");
            return ok ? (true, $"Delivered to {uri.Host}.") : (false, $"{uri.Host} answered HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Outbound notification failed");
            await log.LogAsync(uri.Host, eventName, body, true, "error", ex.Message, null, "outbound");
            return (false, $"Could not reach {uri.Host}.");
        }
    }
}
