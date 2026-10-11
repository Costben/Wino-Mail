#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

/// <summary>
/// Talks to a user-supplied OpenAI-compatible endpoint. Every call is a plain chat completion;
/// the mail content is projected to text segments before it leaves the machine.
/// </summary>
public sealed class CustomAiService : ICustomAiService
{
    private const string DefaultModel = "gpt-4o-mini";

    private readonly HttpClient _httpClient = new();
    private readonly IPreferencesService _preferencesService;

    public CustomAiService(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    public bool IsConfigured
        => _preferencesService.IsCustomAiEnabled && !string.IsNullOrWhiteSpace(_preferencesService.CustomAiBaseUrl);

    public async Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var instruction = string.IsNullOrWhiteSpace(targetLanguage)
            ? "Summarize the following email as clean HTML. Keep the summary concise and return HTML only."
            : $"Summarize the following email as clean HTML in the language with code '{targetLanguage}'. Keep the summary concise and return HTML only.";

        var (succeeded, content, error) = await SendChatAsync(instruction, BuildSegmentsPrompt(segments), cancellationToken).ConfigureAwait(false);

        return succeeded && !string.IsNullOrWhiteSpace(content)
            ? ApiEnvelope<AiSummaryResultDto>.Success(new AiSummaryResultDto(content.Trim()))
            : ApiEnvelope<AiSummaryResultDto>.Failure(error ?? "CustomAiEmptyResponse");
    }

    public async Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        var instruction =
            $"Translate every line into the language with code '{targetLanguage}'. " +
            "Each line starts with an [id] marker that must be copied unchanged. " +
            "Answer with strict JSON only, no code fences: " +
            "{\"sourceLanguage\":\"<detected language code>\",\"translations\":[{\"id\":\"<id>\",\"text\":\"<translated text>\"}]}. " +
            "Keep every id exactly once and preserve the original formatting of each text.";

        var (succeeded, content, error) = await SendChatAsync(instruction, BuildSegmentsPrompt(segments), cancellationToken).ConfigureAwait(false);
        if (!succeeded || string.IsNullOrWhiteSpace(content))
        {
            return ApiEnvelope<AiTranslationResultDto>.Failure(error ?? "CustomAiEmptyResponse");
        }

        CustomAiTranslationResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(StripCodeFences(content), CustomAiJsonContext.Default.CustomAiTranslationResponse);
        }
        catch (JsonException)
        {
            return ApiEnvelope<AiTranslationResultDto>.Failure("CustomAiInvalidResponse");
        }

        if (parsed?.Translations is not { Count: > 0 })
        {
            return ApiEnvelope<AiTranslationResultDto>.Failure("CustomAiEmptyResponse");
        }

        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in parsed.Translations)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && item.Text is not null)
            {
                byId[item.Id] = item.Text;
            }
        }

        var translations = new List<MailTranslation>(segments.Count);
        foreach (var segment in segments)
        {
            if (byId.TryGetValue(segment.Id, out var text))
            {
                translations.Add(new MailTranslation(segment.Id, text));
            }
        }

        if (translations.Count == 0)
        {
            return ApiEnvelope<AiTranslationResultDto>.Failure("CustomAiEmptyResponse");
        }

        var detected = !string.IsNullOrWhiteSpace(parsed.SourceLanguage)
            ? parsed.SourceLanguage!
            : !string.IsNullOrWhiteSpace(sourceLanguage) ? sourceLanguage! : "auto";

        return ApiEnvelope<AiTranslationResultDto>.Success(new AiTranslationResultDto(detected, translations));
    }

    public async Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string language, string context, CancellationToken cancellationToken = default)
    {
        var instruction =
            $"Rewrite the following email with this instruction: {mode}. " +
            $"The message is being {(string.Equals(context, RewriteContexts.Composing, StringComparison.Ordinal) ? "composed" : "read")}. " +
            (string.IsNullOrWhiteSpace(language) ? string.Empty : $"Write it in the language with code '{language}'. ") +
            "Preserve meaning and return clean HTML only.";

        var (succeeded, content, error) = await SendChatAsync(instruction, html, cancellationToken).ConfigureAwait(false);

        return succeeded && !string.IsNullOrWhiteSpace(content)
            ? ApiEnvelope<AiTextResultDto>.Success(new AiTextResultDto(content.Trim()))
            : ApiEnvelope<AiTextResultDto>.Failure(error ?? "CustomAiEmptyResponse");
    }

    private async Task<(bool Succeeded, string? Content, string? Error)> SendChatAsync(string instruction, string userContent, CancellationToken cancellationToken)
    {
        var baseUrl = _preferencesService.CustomAiBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, null, "CustomAiNotConfigured");
        }

        if (string.IsNullOrWhiteSpace(userContent))
        {
            return (false, null, "CustomAiEmptyRequest");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(baseUrl));
            var apiKey = _preferencesService.CustomAiApiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var model = _preferencesService.CustomAiModel?.Trim();
            request.Content = JsonContent.Create(
                new CustomAiChatRequest(
                    string.IsNullOrWhiteSpace(model) ? DefaultModel : model,
                    [
                        new CustomAiMessage("system", instruction),
                        new CustomAiMessage("user", userContent)
                    ]),
                CustomAiJsonContext.Default.CustomAiChatRequest);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            var result = await response.Content
                .ReadFromJsonAsync(CustomAiJsonContext.Default.CustomAiChatResponse, cancellationToken)
                .ConfigureAwait(false);

            var content = result?.Choices is { Count: > 0 } ? result.Choices[0].Message?.Content : null;

            return string.IsNullOrWhiteSpace(content)
                ? (false, null, "CustomAiEmptyResponse")
                : (true, content, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, ex.Message);
        }
    }

    private static string BuildSegmentsPrompt(IReadOnlyList<MailContentSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            builder.Append('[').Append(segment.Id).Append("] ").Append(segment.Text).Append('\n');
        }

        return builder.ToString();
    }

    private static string StripCodeFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].Trim() : body.Trim();
    }

    private static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed, UriKind.Absolute);
        }

        return new Uri(new Uri(trimmed + "/", UriKind.Absolute), "chat/completions");
    }
}

internal sealed record CustomAiChatRequest(string Model, IReadOnlyList<CustomAiMessage> Messages);

internal sealed record CustomAiMessage(string Role, string Content);

internal sealed record CustomAiChatResponse(IReadOnlyList<CustomAiChoice>? Choices);

internal sealed record CustomAiChoice(CustomAiMessage? Message);

internal sealed record CustomAiTranslationResponse(string? SourceLanguage, IReadOnlyList<CustomAiTranslationItem>? Translations);

internal sealed record CustomAiTranslationItem(string? Id, string? Text);

[JsonSerializable(typeof(CustomAiChatRequest))]
[JsonSerializable(typeof(CustomAiChatResponse))]
[JsonSerializable(typeof(CustomAiTranslationResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CustomAiJsonContext : JsonSerializerContext;
