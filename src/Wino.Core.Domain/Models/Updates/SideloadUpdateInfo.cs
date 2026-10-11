#nullable enable
namespace Wino.Core.Domain.Models.Updates;

public sealed record SideloadUpdateInfo(
    string Version,
    string ReleaseName,
    string ReleaseNotes,
    string DownloadUrl,
    string FileName);
