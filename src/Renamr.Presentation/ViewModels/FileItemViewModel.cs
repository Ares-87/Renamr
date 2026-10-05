using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Renamr.Core.Localization;
using Renamr.Core.Models;
using Renamr.Core.Options;

namespace Renamr.Presentation.ViewModels;

/// <summary>Una riga della Diff View.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(RenamePlanEntry entry, string? rootFolder)
    {
        RootFolder = rootFolder;
        Apply(entry);
    }

    /// <summary>Cartella aperta; null se l'elenco ha solo file aggiunti a mano.</summary>
    public string? RootFolder { get; }

    public RenamePlanEntry Entry { get; private set; } = null!;

    [ObservableProperty]
    public partial string OriginalName { get; private set; } = string.Empty;

    /// <summary>Nome proposto; se il template organizza in cartelle, include il percorso relativo.</summary>
    [ObservableProperty]
    public partial string ProposedName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RelativeFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsActionable), nameof(CanChooseMatch), nameof(SuggestsChoice))]
    public partial PlanStatus Status { get; private set; }

    [ObservableProperty]
    public partial string? Detail { get; private set; }

    /// <summary>Riga secondaria: "TMDb · 31/03/1999 · 97%".</summary>
    [ObservableProperty]
    public partial string? MatchSummary { get; private set; }

    public bool IsActionable => Entry.IsActionable;

    /// <summary>Film, serie e musica: cliccando la riga si sceglie tra i risultati o si cerca un altro titolo.</summary>
    public bool CanChooseMatch => !Entry.NameOnly && Status is PlanStatus.Ready or PlanStatus.LowConfidence or PlanStatus.Unchanged or PlanStatus.Error;

    /// <summary>Righe incerte o senza risultato: il link "Scegli…" sotto lo stato invita a farlo.</summary>
    public bool SuggestsChoice => CanChooseMatch && (Status == PlanStatus.LowConfidence || (Status == PlanStatus.Error && Entry.Metadata is null));

    /// <summary>Quale formato usa questa riga (per il menu contestuale "Formato nome").</summary>
    public MediaKind? TemplateKind => (Entry.Metadata?.Kind ?? Entry.Parsed?.Kind) is { } kind and not MediaKind.Unknown
        ? TemplateSettings.KindFor(kind, Entry.Parsed)
        : null;

    public string StatusText => Status switch
    {
        PlanStatus.Pending => Strings.Current.StatusPending,
        PlanStatus.Ready => Strings.Current.StatusReady,
        PlanStatus.LowConfidence => Strings.Current.StatusLowConfidence,
        PlanStatus.Unchanged => Strings.Current.StatusUnchanged,
        PlanStatus.Error => Strings.Current.StatusError,
        PlanStatus.Done => Entry.Error is null ? Strings.Current.StatusDone : Strings.Current.StatusDoneWithWarnings,
        PlanStatus.Simulated => Strings.Current.StatusSimulated,
        PlanStatus.Skipped => Strings.Current.StatusSkipped,
        _ => Status.ToString(),
    };

    /// <summary>Lingua dell'interfaccia cambiata.</summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(StatusText));
        Apply(Entry); // "scelto da te" e le date seguono la nuova lingua
    }

    public void Apply(RenamePlanEntry entry)
    {
        Entry = entry;
        OriginalName = entry.SourceName;
        // Un file aggiunto da un'altra cartella mostra la sua cartella per intero.
        RelativeFolder = IsOutsideRoot ? Path.GetDirectoryName(entry.SourcePath) ?? string.Empty : Relative(Path.GetDirectoryName(entry.SourcePath));
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
                entry.ManualMatch ? Strings.Current.MatchManual
                    : entry.Confidence > 0 ? entry.Confidence.ToString("P0", CultureInfo.CurrentCulture) : null,
            }.Where(s => !string.IsNullOrEmpty(s)))
            : null;
        OnPropertyChanged(nameof(IsActionable));
        OnPropertyChanged(nameof(CanChooseMatch));
        OnPropertyChanged(nameof(SuggestsChoice));
        OnPropertyChanged(nameof(TemplateKind));
        OnPropertyChanged(nameof(StatusText));
    }

    private bool IsOutsideRoot => Entry.Root is { } own && !string.Equals(own, RootFolder, StringComparison.Ordinal);

    private string Relative(string? path)
    {
        var baseFolder = Entry.Root ?? RootFolder;
        if (path is null || baseFolder is null)
        {
            return string.Empty;
        }
        var relative = Path.GetRelativePath(baseFolder, path);
        return relative is "." ? string.Empty : relative;
    }
}
