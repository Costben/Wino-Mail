#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Mail.WinUI.Services;

public sealed class SideloadUpdateService : ISideloadUpdateService
{
    private const string DefaultFeedUrl = "https://api.github.com/repos/Costben/Wino-Mail/releases";
    private const string ReleaseTagPrefix = "sideload-v";

    private readonly HttpClient _httpClient = new();
    private readonly IPreferencesService _preferencesService;
    private readonly INativeAppService _nativeAppService;
    private readonly IWinoLogger _logger;

    public SideloadUpdateService(IPreferencesService preferencesService,
                                 INativeAppService nativeAppService,
                                 IWinoLogger logger)
    {
        _preferencesService = preferencesService;
        _nativeAppService = nativeAppService;
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Wino-Mail-Sideload-Updater/1.0");
    }

    public bool IsSideloaded
    {
        get
        {
            try
            {
                return Package.Current.SignatureKind == PackageSignatureKind.Developer;
            }
            catch (Exception ex)
            {
                _logger.CaptureException(ex, nameof(IsSideloaded));
                return false;
            }
        }
    }

    public async Task<SideloadUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsSideloaded || !_preferencesService.IsSideloadUpdateCheckEnabled)
                return null;

            if (!Version.TryParse(_nativeAppService.GetFullAppVersion(), out var currentVersion))
                return null;

            var feedUrl = _preferencesService.SideloadUpdateFeedUrl?.Trim();
            if (string.IsNullOrWhiteSpace(feedUrl))
                feedUrl = DefaultFeedUrl;

            using var response = await _httpClient.GetAsync(feedUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            // Releases feed is ordered newest-first, so the first newer entry with an .msix wins.
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object)
                    continue;

                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    continue;

                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                string? downloadUrl = null;
                string? fileName = null;

                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(assetName)
                        || !assetName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                        continue;

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    fileName = assetName;
                    break;
                }

                if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(fileName))
                    continue;

                var releaseVersion = ParseReleaseVersion(release, fileName);
                if (releaseVersion == null || releaseVersion <= currentVersion)
                    continue;

                var releaseName = release.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString())
                    ? nameProp.GetString()!
                    : releaseVersion.ToString();
                var releaseNotes = release.TryGetProperty("body", out var bodyProp)
                    ? bodyProp.GetString() ?? string.Empty
                    : string.Empty;

                return new SideloadUpdateInfo(releaseVersion.ToString(), releaseName, releaseNotes, downloadUrl, fileName);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.CaptureException(ex, nameof(CheckForUpdateAsync));
            return null;
        }
    }

    public async Task<bool> DownloadAndLaunchUpdateAsync(SideloadUpdateInfo info, CancellationToken cancellationToken = default)
    {
        try
        {
            var tempFolder = ApplicationData.Current.TemporaryFolder;
            var file = await tempFolder.CreateFileAsync(info.FileName, CreationCollisionOption.ReplaceExisting).AsTask(cancellationToken).ConfigureAwait(false);

            using var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var fileStream = await file.OpenStreamForWriteAsync().ConfigureAwait(false);
            await networkStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Opens the package in App Installer. Same cert as the installed app,
            // so this is an in-place upgrade without signature prompts.
            await _nativeAppService.LaunchFileAsync(file.Path).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.CaptureException(ex, nameof(DownloadAndLaunchUpdateAsync));
            return false;
        }
    }

    private static Version? ParseReleaseVersion(JsonElement release, string fileName)
    {
        if (release.TryGetProperty("tag_name", out var tagElement))
        {
            var tag = tagElement.GetString() ?? string.Empty;
            var prefixIndex = tag.IndexOf(ReleaseTagPrefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex >= 0)
            {
                var remainder = tag.Substring(prefixIndex + ReleaseTagPrefix.Length);
                var dashIndex = remainder.IndexOf('-');
                var versionText = dashIndex >= 0 ? remainder.Substring(0, dashIndex) : remainder;
                if (Version.TryParse(versionText, out var tagVersion))
                    return tagVersion;
            }
        }

        // Fallback for assets named like Wino-Sideload_2.0.30.0_<sha>.msix.
        foreach (var token in fileName.Split('_'))
        {
            if (Version.TryParse(token, out var fileVersion))
                return fileVersion;
        }

        return null;
    }
}
