#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Local OpenAI-compatible replacement for the Wino AI backend. When it is configured, AI
/// actions never reach the Wino service and the Wino Intelligence entitlement gates do not apply.
/// </summary>
public interface ICustomAiService
{
    bool IsConfigured { get; }

    Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default);

    Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);

    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string language, string context, CancellationToken cancellationToken = default);
}
