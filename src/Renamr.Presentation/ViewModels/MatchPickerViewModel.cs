using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renamr.Core.Localization;
using Renamr.Core.Models;

namespace Renamr.Presentation.ViewModels;

/// <summary>
/// Finestra "Scegli la corrispondenza" di una riga: i risultati che i database hanno già dato (anche quelli scartati
/// perché incerti) e una ricerca libera con titolo, anno e tipo scelti dall'utente. La scelta la applica chi apre la finestra.
/// </summary>
public sealed partial class MatchPickerViewModel : ObservableObject
{
    private static readonly MediaKind[] KindOrder = [MediaKind.Movie, MediaKind.Episode, MediaKind.Anime, MediaKind.Music];
    private readonly Func<MediaQuery, CancellationToken, Task<MatchResult>> _search;
    private readonly string _filePath;

    public MatchPickerViewModel(FileItemViewModel item, Func<MediaQuery, CancellationToken, Task<MatchResult>> search)
    {
        ArgumentNullException.ThrowIfNull(item);
        _search = search;
        var entry = item.Entry;
        _filePath = entry.SourcePath;
        FileName = entry.SourceName;

        var parsed = entry.Parsed;
        // Un titolo offuscato ("B0N3 L4K3") si propone già decodificato ("Bone Lake").
        Query = parsed is null ? Path.GetFileNameWithoutExtension(entry.SourcePath) : Core.Parsing.LeetSpeak.Decode(parsed.Title);
        Year = parsed?.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Season = parsed?.Season?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Episode = (parsed?.FirstEpisode)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var kind = entry.Metadata?.Kind ?? parsed?.Kind ?? MediaKind.Movie;
        KindIndex = Math.Max(0, Array.IndexOf(KindOrder, kind));

        ShowChoices(entry.Candidates, entry.Metadata);
    }

    public string FileName { get; }

    public IReadOnlyList<string> Kinds { get; } =
        [Strings.Current.PickKindMovie, Strings.Current.PickKindEpisode, Strings.Current.PickKindAnime, Strings.Current.PickKindMusic];

    public ObservableCollection<MatchChoiceViewModel> Choices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial MatchChoiceViewModel? SelectedChoice { get; set; }

    public bool CanApply => SelectedChoice is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Year { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Season { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Episode { get; set; } = string.Empty;

    /// <summary>0 film, 1 episodio, 2 anime, 3 musica.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSeries))]
    public partial int KindIndex { get; set; }

    /// <summary>Stagione ed episodio servono solo per serie e anime.</summary>
    public bool IsSeries => SelectedKind is MediaKind.Episode or MediaKind.Anime;

    public MediaKind SelectedKind => KindOrder[Math.Clamp(KindIndex, 0, KindOrder.Length - 1)];

    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    private bool CanSearch() => !string.IsNullOrWhiteSpace(Query);

    [RelayCommand(CanExecute = nameof(CanSearch), IncludeCancelCommand = true)]
    private async Task SearchAsync(CancellationToken ct)
    {
        var query = new MediaQuery
        {
            Kind = SelectedKind,
            Title = Query.Trim(),
            Year = Number(Year),
            Season = IsSeries ? Number(Season) : null,
            Episode = IsSeries ? Number(Episode) : null,
            AbsoluteEpisode = SelectedKind == MediaKind.Anime && Number(Season) is null ? Number(Episode) : null,
            FilePath = _filePath,
        };
        IsSearching = true;
        Message = Strings.Current.MatchPickerSearching;
        try
        {
            var result = await _search(query, ct);
            ShowChoices(result.Alternatives, null);
            SelectedChoice = Choices.FirstOrDefault(); // il più probabile, pronto per "Usa questo"
            if (result.Alternatives.Count == 0 && result.Outcome == MatchOutcome.ProviderFailure)
            {
                Message = Strings.Current.Format(nameof(Strings.MatchPickerFailed), string.Join(" · ", result.Trace));
            }
        }
        catch (OperationCanceledException)
        {
            Message = null;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void ShowChoices(IEnumerable<MatchCandidate> candidates, MediaMetadata? current)
    {
        Choices.Clear();
        foreach (var candidate in candidates)
        {
            Choices.Add(new MatchChoiceViewModel(candidate, candidate.Metadata.IsSameEntry(current)));
        }
        SelectedChoice = Choices.FirstOrDefault(c => c.IsCurrent);
        Message = Choices.Count == 0 ? Strings.Current.MatchPickerNone : Strings.Current.Format(nameof(Strings.MatchPickerCount), Choices.Count);
    }

    private static int? Number(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
}

/// <summary>Una voce della lista dei risultati: "The Matrix (1999)" e sotto "Film · TMDb · 31/03/1999 · 97%".</summary>
public sealed class MatchChoiceViewModel(MatchCandidate candidate, bool isCurrent)
{
    public MatchCandidate Candidate { get; } = candidate;

    /// <summary>Il risultato usato adesso dalla riga.</summary>
    public bool IsCurrent { get; } = isCurrent;

    public string Title
    {
        get
        {
            var md = Candidate.Metadata;
            var main = md.Kind == MediaKind.Music && !string.IsNullOrWhiteSpace(md.Artist) ? $"{md.Artist} – {md.Title}" : md.Title;
            return md.Year is { } year ? $"{main} ({year.ToString(CultureInfo.InvariantCulture)})" : main;
        }
    }

    /// <summary>Episodio o album, quando c'è.</summary>
    public string? Subtitle
    {
        get
        {
            var md = Candidate.Metadata;
            if (md.Season is { } s && md.Episode is { } e)
            {
                var code = $"S{s:00}E{e:00}";
                return string.IsNullOrWhiteSpace(md.EpisodeTitle) ? code : $"{code} · {md.EpisodeTitle}";
            }
            if (md.AbsoluteEpisode is { } abs)
            {
                return string.IsNullOrWhiteSpace(md.EpisodeTitle) ? $"#{abs}" : $"#{abs} · {md.EpisodeTitle}";
            }
            return md.Kind == MediaKind.Music ? md.Album : md.OriginalTitle is { } original && original != md.Title ? original : null;
        }
    }

    public string Details
    {
        get
        {
            var md = Candidate.Metadata;
            var kind = md.Kind switch
            {
                MediaKind.Movie => Strings.Current.PickKindMovie,
                MediaKind.Episode => Strings.Current.PickKindEpisode,
                MediaKind.Anime => Strings.Current.PickKindAnime,
                MediaKind.Music => Strings.Current.PickKindMusic,
                _ => null,
            };
            return string.Join(" · ", new[]
            {
                kind,
                md.Provider,
                md.ReleaseDate?.ToString("d", CultureInfo.CurrentCulture),
                Candidate.Confidence > 0 ? Candidate.Confidence.ToString("P0", CultureInfo.CurrentCulture) : null,
                IsCurrent ? Strings.Current.MatchPickerCurrent : null,
            }.Where(t => !string.IsNullOrEmpty(t)));
        }
    }

    /// <summary>Trama (tooltip): aiuta a distinguere titoli uguali.</summary>
    public string? Overview => Candidate.Metadata.Overview;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
}
