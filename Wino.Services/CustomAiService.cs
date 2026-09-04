#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class CustomAiService : ICustomAiService
{
    private readonly HttpClient _httpClient = new();
    private readonly IPreferencesService _preferencesService;

    public CustomAiService(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    public bool IsConfigured => _preferencesService.IsCustomAiEnabled && !string.IsNullOrWhiteSpace(_preferencesService.CustomAiBaseUrl);

    public Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, string targetLanguage, CancellationToken cancellationToken = default)
        => SendChatRequestAsync(
            "Summarize the following email as clean HTML. Keep the summary concise and use the target language when possible.",
            html,
            targetLanguage,
            cancellationToken);

    public Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default)
        => SendChatRequestAsync(
            "Translate the following email to the target language. Preserve meaning and return clean HTML only.",
            html,
            targetLanguage,
            cancellationToken);

    public Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default)
        => SendChatRequestAsync(
            $"Rewrite the following email with this instruction: {mode}. Preserve meaning and return clean HTML only.",
            html,
            string.Empty,
            cancellationToken);

    private async Task<ApiEnvelope<AiTextResultDto>> SendChatRequestAsync(string instruction, string html, string targetLanguage, CancellationToken cancellationToken)
    {
        var baseUrl = _preferencesService.CustomAiBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ApiEnvelope<AiTextResultDto>.Failure("CustomAiNotConfigured");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(baseUrl));
            var apiKey = _preferencesService.CustomAiApiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            request.Content = JsonContent.Create(
                new CustomAiChatRequest(
                    string.IsNullOrWhiteSpace(_preferencesService.CustomAiModel) ? "gpt-4o-mini" : _preferencesService.CustomAiModel.Trim(),
                    [
                        new CustomAiMessage("system", instruction),
                        new CustomAiMessage("user", CreateUserPrompt(html, targetLanguage))
                    ]),
                CustomAiJsonContext.Default.CustomAiChatRequest);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync(CustomAiJsonContext.Default.CustomAiChatResponse, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ApiEnvelope<AiTextResultDto>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            var content = result?.Choices is { Count: > 0 }
                ? result.Choices[0].Message?.Content
                : null;

            return string.IsNullOrWhiteSpace(content)
                ? ApiEnvelope<AiTextResultDto>.Failure("CustomAiEmptyResponse")
                : ApiEnvelope<AiTextResultDto>.Success(new AiTextResultDto(content.Trim()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ApiEnvelope<AiTextResultDto>.Failure(ex.Message);
        }
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

    private static string CreateUserPrompt(string html, string targetLanguage)
        => string.IsNullOrWhiteSpace(targetLanguage)
            ? html
            : $"Target language: {targetLanguage}\n\nHTML:\n{html}";
}

internal sealed record CustomAiChatRequest(string Model, IReadOnlyList<CustomAiMessage> Messages);

internal sealed record CustomAiMessage(string Role, string Content);

internal sealed record CustomAiChatResponse(IReadOnlyList<CustomAiChoice>? Choices);

internal sealed record CustomAiChoice(CustomAiMessage? Message);

[JsonSerializable(typeof(CustomAiChatRequest))]
[JsonSerializable(typeof(CustomAiChatResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class CustomAiJsonContext : JsonSerializerContext;
