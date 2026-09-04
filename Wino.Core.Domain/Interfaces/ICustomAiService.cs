#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain.Interfaces;

public interface ICustomAiService
{
    bool IsConfigured { get; }

    Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, string targetLanguage, CancellationToken cancellationToken = default);

    Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default);

    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default);
}
