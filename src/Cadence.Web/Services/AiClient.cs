using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Cadence.Web.Services;

public sealed record AiProviderInfo(string Id, string Label, string KeyHint, string KeyUrl, string DefaultModel);

public sealed record AiStatus(string Provider, bool KeyConfigured, string? KeyHint, bool Enabled, string Model)
{
    public bool Active => Provider != "none" && KeyConfigured && Enabled;
    public string ProviderLabel => AiClient.Providers.FirstOrDefault(p => p.Id == Provider)?.Label ?? "Not connected";
    public string ModelLabel => AiClient.AnthropicModels.FirstOrDefault(m => m.Id == Model).Label ?? Model;
}

/// <summary>
/// The AI layer behind research suggestions. The team connects a provider and pastes its own API key in Settings;
/// until then nothing is sent anywhere. Keys are stored encrypted (<see cref="SecretBox"/>) and never read from
/// environment variables, so a server's own credentials are never used by accident.
///
/// Providers:
///  - Anthropic: Claude through the official Anthropic SDK, with structured outputs (and a server-side refusal fallback on Claude Opus 5).
///  - OpenRouter: one key for many model families (Claude, GPT, Gemini, Llama...), through its chat-completions API with a JSON schema.
/// </summary>
public sealed class AiClient(SettingsService settings, SecretBox secrets, IHttpClientFactory httpFactory, ILogger<AiClient> logger)
{
    public static readonly AiProviderInfo[] Providers =
    [
        new("anthropic", "Anthropic (Claude)", "sk-ant-...", "https://console.anthropic.com/settings/keys", "claude-opus-5"),
        new("openrouter", "OpenRouter", "sk-or-...", "https://openrouter.ai/settings/keys", "openrouter/auto"),
    ];

    public static readonly (string Id, string Label, string Note)[] AnthropicModels =
    [
        ("claude-opus-5", "Claude Opus 5", "Best research quality. Default."),
        ("claude-sonnet-5", "Claude Sonnet 5", "Faster and cheaper, still strong."),
        ("claude-haiku-4-5", "Claude Haiku 4.5", "Fastest and cheapest, for high volume."),
    ];

    public const string DefaultModel = "claude-opus-5";

    public async Task<AiStatus> StatusAsync()
    {
        var s = await settings.GetAsync();
        var hasKey = !string.IsNullOrEmpty(secrets.Unprotect(s.AiKeyEncrypted));
        return new AiStatus(s.AiProvider, hasKey, hasKey ? s.AiKeyHint : null, s.AiEnabled, s.AiModel);
    }

    public static string NormalizeModel(string provider, string? model)
    {
        model = model?.Trim();
        if (provider == "anthropic") return AnthropicModels.Any(m => m.Id == model) ? model! : DefaultModel;
        if (provider == "openrouter")
            return !string.IsNullOrEmpty(model) && model.Length <= 120 && model.All(c => char.IsLetterOrDigit(c) || "/-._:".Contains(c)) ? model : "openrouter/auto";
        return model ?? "";
    }

    /// <summary>
    /// Sends one prompt and returns JSON constrained by <paramref name="schema"/>. Returns null when AI is not connected
    /// or the model declined; provider errors become an <see cref="AiException"/> with a message the UI can show.
    /// </summary>
    public async Task<JsonElement?> CompleteJsonAsync(string system, string prompt, Dictionary<string, JsonElement> schema, CancellationToken ct = default)
    {
        var s = await settings.GetAsync();
        var key = secrets.Unprotect(s.AiKeyEncrypted);
        if (s.AiProvider == "none" || !s.AiEnabled || string.IsNullOrEmpty(key)) return null;

        return s.AiProvider switch
        {
            "anthropic" => await AnthropicAsync(key, s.AiModel, system, prompt, schema, ct),
            "openrouter" => await OpenRouterAsync(key, s.AiModel, system, prompt, schema, ct),
            _ => null,
        };
    }

    private async Task<JsonElement?> AnthropicAsync(string key, string model, string system, string prompt, Dictionary<string, JsonElement> schema, CancellationToken ct)
    {
        try
        {
            AnthropicClient client = new() { ApiKey = key };
            var isOpus5 = model == "claude-opus-5";
            // Claude Opus 5 requests carry a server-side refusal fallback: a rare policy decline is re-served by
            // Claude Opus 4.8 inside the same call instead of returning nothing.
            var parameters = new MessageCreateParams
            {
                Model = model,
                MaxTokens = 4000,
                System = system,
                Messages = [new BetaMessageParam { Role = Role.User, Content = prompt }],
                OutputConfig = new BetaOutputConfig { Format = new BetaJsonOutputFormat { Schema = schema } },
                Betas = isOpus5 ? [AnthropicBeta.ServerSideFallback2026_06_01] : null,
                Fallbacks = isOpus5 ? (BetaFallbacksParam)new List<BetaFallbackParam> { new(Anthropic.Models.Messages.Model.ClaudeOpus4_8) } : null,
            };
            var response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);
            if (response.StopReason == BetaStopReason.Refusal)
            {
                logger.LogWarning("Claude declined a research request");
                return null;
            }
            return ParseJson(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().FirstOrDefault()?.Text);
        }
        catch (AnthropicUnauthorizedException) { throw new AiException("Anthropic rejected the API key. Paste a valid key in Settings, AI."); }
        catch (AnthropicRateLimitException) { throw new AiException("Anthropic is rate limiting this key right now. Try again in a minute."); }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning(ex, "Anthropic request failed");
            throw new AiException("Anthropic could not complete the request: " + ex.Message);
        }
    }

    private async Task<JsonElement?> OpenRouterAsync(string key, string model, string system, string prompt, Dictionary<string, JsonElement> schema, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("openrouter");
        var messages = new object[] { new { role = "system", content = system }, new { role = "user", content = prompt } };

        async Task<HttpResponseMessage> SendAsync(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("X-Title", "Cadence");
            return await http.SendAsync(request, ct);
        }

        try
        {
            var response = await SendAsync(new
            {
                model, messages, max_tokens = 4000,
                response_format = new { type = "json_schema", json_schema = new { name = "cadence_research", strict = true, schema } },
            });
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                // Some models do not support JSON schemas: ask for plain JSON and validate on our side.
                response.Dispose();
                var schemaText = JsonSerializer.Serialize(schema);
                response = await SendAsync(new
                {
                    model, max_tokens = 4000, response_format = new { type = "json_object" },
                    messages = new object[] { new { role = "system", content = system + "\nAnswer with one JSON object that matches this JSON Schema:\n" + schemaText }, new { role = "user", content = prompt } },
                });
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                switch ((int)response.StatusCode)
                {
                    case 401: throw new AiException("OpenRouter rejected the API key. Paste a valid key in Settings, AI.");
                    case 402: throw new AiException("The OpenRouter account has no credits left for this model.");
                    case 429: throw new AiException("OpenRouter is rate limiting this key right now. Try again in a minute.");
                }
                if (!response.IsSuccessStatusCode) throw new AiException($"OpenRouter answered HTTP {(int)response.StatusCode}. Check the model name in Settings, AI.");
                using var doc = JsonDocument.Parse(text);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                return ParseJson(content);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OpenRouter request failed");
            throw new AiException("Could not reach OpenRouter.");
        }
    }

    private JsonElement? ParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The model returned text that was not valid JSON");
            return null;
        }
    }

    /// <summary>Cheap round trip used by the Settings page to prove the key and model work.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        var status = await StatusAsync();
        if (status.Provider == "none") return (false, "Choose a provider first.");
        if (!status.KeyConfigured) return (false, "Paste an API key first.");
        if (!status.Enabled) return (false, "AI research is switched off.");
        var schema = new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["properties"] = JsonSerializer.SerializeToElement(new { ok = new { type = "boolean" } }),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "ok" }),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        };
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await CompleteJsonAsync("Reply with the requested JSON only.", "Return {\"ok\": true}.", schema, ct);
            return result is { } r && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                ? (true, $"Connected to {status.ProviderLabel}, {status.ModelLabel}, in {sw.ElapsedMilliseconds} ms.")
                : (false, "The model answered, but not with the expected JSON. Try another model.");
        }
        catch (AiException ex) { return (false, ex.Message); }
    }
}

public sealed class AiException(string message) : Exception(message);
