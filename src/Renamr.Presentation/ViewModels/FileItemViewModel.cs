using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Renamr.Core.Models;
using Renamr.Core.Options;

namespace Renamr.Presentation.ViewModels;

/// <summary>Una riga della Diff View.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(RenamePlanEntry entry, string rootFolder)
    {
        RootFolder = rootFolder;
        Apply(entry);
    }

    public string RootFolder { get; }

    public RenamePlanEntry Entry { get; private set; } = null!;

    [ObservableProperty]
    public partial string OriginalName { get; private set; } = string.Empty;

    /// <summary>Nome proposto; se il template organizza in cartelle, include il percorso relativo.</summary>
    [ObservableProperty]
    public partial string ProposedName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RelativeFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsActionable))]
    public partial PlanStatus Status { get; private set; }

    [ObservableProperty]
    public partial string? Detail { get; private set; }

    /// <summary>Riga secondaria: "TMDb · 31/03/1999 · 97%".</summary>
    [ObservableProperty]
    public partial string? MatchSummary { get; private set; }

    public bool IsActionable => Entry.IsActionable;

    /// <summary>Quale formato usa questa riga (per il menu contestuale "Formato nome").</summary>
    public MediaKind? TemplateKind => (Entry.Metadata?.Kind ?? Entry.Parsed?.Kind) is { } kind and not MediaKind.Unknown
        ? TemplateSettings.KindFor(kind, Entry.Parsed)
        : null;

    public string StatusText => Status switch
    {
        PlanStatus.Pending => "In attesa",
        PlanStatus.Ready => "Pronto",
        PlanStatus.LowConfidence => "Bassa confidenza",
        PlanStatus.Unchanged => "Già corretto",
        PlanStatus.Error => "Errore",
        PlanStatus.Done => Entry.Error is null ? "Fatto" : "Fatto con avvisi",
        PlanStatus.Simulated => "Simulato",
        PlanStatus.Skipped => "Saltato",
        _ => Status.ToString(),
    };

    public void Apply(RenamePlanEntry entry)
    {
        Entry = entry;
        OriginalName = entry.SourceName;
        RelativeFolder = Relative(Path.GetDirectoryName(entry.SourcePath));
        ProposedName = entry.TargetPath is null
            ? "—"
            : string.Equals(Path.GetDirectoryName(entry.TargetPath), Path.GetDirectoryName(entry.SourcePath), StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(entry.TargetPath)
                : Relative(entry.TargetPath);
        Status = entry.Status;
        Detail = entry.Error?.Message;
        MatchSummary = entry.Metadata is { } md
            ? string.Join(" · ", new[]
            {
                md.Provider,
                md.ReleaseDate?.ToString("d", CultureInfo.CurrentCulture) ?? md.Year?.ToString(CultureInfo.CurrentCulture),
                entry.Confidence > 0 ? entry.Confidence.ToString("P0", CultureInfo.CurrentCulture) : null,
            }.Where(s => !string.IsNullOrEmpty(s)))
            : null;
        OnPropertyChanged(nameof(IsActionable));
        OnPropertyChanged(nameof(TemplateKind));
        OnPropertyChanged(nameof(StatusText));
    }

    private string Relative(string? path) =>
        path is null ? string.Empty : Path.GetRelativePath(RootFolder, path) is "." ? string.Empty : Path.GetRelativePath(RootFolder, path);
}
