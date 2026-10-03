using Renamr.Core.Abstractions;
using Renamr.Services.IO;

namespace Renamr.Services.Pipeline;

/// <summary>Elenca i file multimediali sotto la radice, senza mai seguire junction o symlink.</summary>
public sealed class MediaScanner(ISettingsStore settings)
{
    public IReadOnlyList<string> Scan(PathBoundary boundary)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // ReparsePoint: una junction dentro la libreria non porta la scansione fuori dalla radice.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var s = settings.Current;
        return Directory.EnumerateFiles(boundary.Root, "*", options)
            .Where(f => s.VideoExtensions.Contains(Path.GetExtension(f)) || s.AudioExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetFileName(f).Contains(".renamr-", StringComparison.OrdinalIgnoreCase)) // temporanei nostri
            .Where(boundary.IsStrictDescendant)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
