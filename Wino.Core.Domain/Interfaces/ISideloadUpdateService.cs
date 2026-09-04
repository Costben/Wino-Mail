#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Core.Domain.Interfaces;

public interface ISideloadUpdateService
{
    /// <summary>
    /// Whether the running package is a self-signed sideload build (Store update check does not apply).
    /// </summary>
    bool IsSideloaded { get; }

    /// <summary>
    /// Checks the configured GitHub releases feed for a newer .msix. Returns null when there is nothing to install.
    /// </summary>
    Task<SideloadUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the update package and opens it with App Installer. Same signing cert = in-place upgrade, no signature issues.
    /// </summary>
    Task<bool> DownloadAndLaunchUpdateAsync(SideloadUpdateInfo info, CancellationToken cancellationToken = default);
}
