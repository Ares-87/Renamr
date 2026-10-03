using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Core.Templating;
using Renamr.Presentation.Messages;
using Renamr.Presentation.Services;
using Renamr.Services.Matching;
using Renamr.Services.Pipeline;

namespace Renamr.Presentation.ViewModels;

/// <summary>
/// ViewModel della finestra principale: governa il flusso Selezione ➔ Anteprima ➔ Azione.
/// Gli aggiornamenti arrivano via <see cref="IProgress{T}"/>: creato sul thread UI, riporta sul thread UI
/// (DispatcherQueueSynchronizationContext in WinUI) senza Dispatcher espliciti nel ViewModel.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly IFolderPickerService _folderPicker;
    private readonly IMessenger _messenger;
    private readonly Dictionary<string, FileItemViewModel> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastJournal;
    private IReadOnlyList<RenamePlanEntry> _plan = [];
    private bool _syncingLanguage;

    public MainViewModel(IServiceProvider services, IFolderPickerService folderPicker, IMessenger messenger, IssuesViewModel issues)
    {
        _services = services;
        _folderPicker = folderPicker;
        _messenger = messenger;
        Issues = issues;
        SyncFromSettings();
    }

    public IssuesViewModel Issues { get; }

    /// <summary>"Renamr v1.1.0": accanto al nome, per capire al volo se si sta usando l'ultima revisione.</summary>
    public string AppTitle => AppInfo.Title;

    /// <summary>Versione con commit, nel tooltip.</summary>
    public string AppDetails => AppInfo.Details;

    public IReadOnlyList<LanguageOption> Languages => LanguageOption.All;

    /// <summary>Selettore rapido della lingua dei titoli: cambiarla rifà subito le ricerche.</summary>
    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; } = LanguageOption.All[0];

    /// <summary>Spiega perché alcuni titoli restano in inglese (manca la chiave TMDb).</summary>
    [ObservableProperty]
    public partial string? LanguageHint { get; private set; }

    [ObservableProperty]
    public partial bool IsLanguageHintOpen { get; set; }

    /// <summary>Database che hanno dato errore nell'ultima analisi (es. chiave TMDb rifiutata): i titoli sono arrivati da altre fonti.</summary>
    [ObservableProperty]
    public partial string? ProviderWarning { get; private set; }

    [ObservableProperty]
    public partial bool IsProviderWarningOpen { get; set; }

    private ISettingsStore Settings => _services.GetRequiredService<ISettingsStore>();

    public ObservableCollection<FileItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectPhase), nameof(IsPreviewVisible), nameof(IsBusy), nameof(PrimaryActionText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial AppPhase Phase { get; private set; } = AppPhase.SelectFolder;

    [ObservableProperty]
    public partial string? RootFolder { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue))]
    public partial int Processed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue))]
    public partial int Total { get; private set; }

    [ObservableProperty]
    public partial string? CurrentFile { get; private set; }

    [ObservableProperty]
    public partial string? ProgressPhase { get; private set; }

    /// <summary>Simulazione: nessuna modifica su disco.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText))]
    public partial bool IsDryRun { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(ActionableCount))]
    public partial bool IncludeLowConfidence { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(ActionableCount))]
    public partial int ReadyCount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(ActionableCount))]
    public partial int LowConfidenceCount { get; private set; }

    [ObservableProperty]
    public partial int ErrorCount { get; private set; }

    // InfoBar di riepilogo (fase Completed)
    [ObservableProperty]
    public partial bool IsSummaryOpen { get; set; }

    [ObservableProperty]
    public partial string? SummaryTitle { get; private set; }

    [ObservableProperty]
    public partial string? SummaryMessage { get; private set; }

    [ObservableProperty]
    public partial bool SummaryHasErrors { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool CanUndo { get; private set; }

    /// <summary>Dopo una simulazione il piano resta valido: si può applicare senza rifare l'analisi.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool LastRunWasDryRun { get; private set; }

    public bool IsSelectPhase => Phase == AppPhase.SelectFolder;
    public bool IsPreviewVisible => Phase is AppPhase.Preview or AppPhase.Running or AppPhase.Completed or AppPhase.Analyzing;
    public bool IsBusy => Phase is AppPhase.Analyzing or AppPhase.Running;
    public int ActionableCount => ReadyCount + (IncludeLowConfidence ? LowConfidenceCount : 0);
    public string ProgressText => $"{Processed} / {Total}";
    public double ProgressValue => Total == 0 ? 0 : 100.0 * Processed / Total;
    public string PrimaryActionText => IsDryRun ? "Avvia Simulazione" : "Avvia Ridenominazione";

    // ---- Fase 1: selezione ------------------------------------------------------------------

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var folder = await _folderPicker.PickFolderAsync();
        if (folder is not null)
        {
            await OpenFolderCommand.ExecuteAsync(folder);
        }
    }

    /// <summary>Usato sia dal selettore sia dal drag &amp; drop.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task OpenFolderAsync(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || IsBusy)
        {
            return;
        }

        // Normalizzazione immediata: da qui in avanti esiste solo il percorso canonico.
        RootFolder = Path.GetFullPath(folder);
        Items.Clear();
        _bySource.Clear();
        _plan = [];
        IsSummaryOpen = false;
        CanUndo = false;
        LastRunWasDryRun = false;
        _messenger.Send(new RunStartedMessage("Analisi"));
        Phase = AppPhase.Analyzing;

        var progress = new Progress<RenameProgress>(OnProgress);
        var health = _services.GetService<ProviderHealth>();
        health?.Reset();
        ProviderWarning = null;
        IsProviderWarningOpen = false;
        try
        {
            var planner = _services.GetRequiredService<RenamePlanner>();
            var plan = await Task.Run(() => planner.PlanAsync(RootFolder, progress, ct), ct);
            _plan = plan;

            // Ricostruzione finale nell'ordine stabile del piano (il progresso arriva in ordine sparso).
            Items.Clear();
            _bySource.Clear();
            foreach (var entry in plan)
            {
                AddOrUpdate(entry, publishIssue: false);
                PublishIssue(entry, "Analisi");
            }
            RecountStatuses();
            ProviderWarning = DescribeFailures(health);
            IsProviderWarningOpen = ProviderWarning is not null;
            Phase = AppPhase.Preview;
        }
        catch (OperationCanceledException)
        {
            Phase = Items.Count > 0 ? AppPhase.Preview : AppPhase.SelectFolder;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            _messenger.Send(new FileIssueMessage(folder, RenamrError.From(RenamrErrorCode.AccessDenied, ex.Message), "Analisi"));
            Phase = AppPhase.SelectFolder;
        }
    }

    // ---- Fase 3: azione unica -----------------------------------------------------------------

    private bool CanRun() => (Phase is AppPhase.Preview || (Phase is AppPhase.Completed && LastRunWasDryRun)) && ActionableCount > 0;

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        var root = RootFolder!;
        var options = new RenameRunOptions { DryRun = IsDryRun, IncludeLowConfidence = IncludeLowConfidence };
        var plan = _plan;

        _messenger.Send(new RunStartedMessage(IsDryRun ? "Simulazione" : "Ridenominazione"));
        Phase = AppPhase.Running;

        var progress = new Progress<RenameProgress>(OnProgress);
        var executor = _services.GetRequiredService<RenameExecutor>();
        IReadOnlyList<RenamePlanEntry> results;
        try
        {
            results = await Task.Run(() => executor.ExecuteAsync(root, plan, options, progress, ct), CancellationToken.None);
        }
        finally
        {
            Phase = AppPhase.Completed;
        }

        foreach (var entry in results)
        {
            AddOrUpdate(entry, publishIssue: entry.Status is PlanStatus.Done or PlanStatus.Error or PlanStatus.Skipped);
        }
        RecountStatuses();

        var ok = results.Count(r => r.Status is PlanStatus.Done or PlanStatus.Simulated);
        var failed = results.Count(r => r.Status == PlanStatus.Error && r.Error is { IsWarning: false });
        var warnings = results.Count(r => r.Status == PlanStatus.Done && r.Error is not null);
        _lastJournal = options.DryRun ? null : _services.GetRequiredService<RenameJournal>().CurrentFile;
        CanUndo = !options.DryRun && ok > 0;
        LastRunWasDryRun = options.DryRun;
        IsDryRun = false; // il prossimo click applica davvero

        SummaryHasErrors = failed > 0;
        SummaryTitle = options.DryRun ? "Simulazione completata" : "Ridenominazione completata";
        SummaryMessage = options.DryRun
            ? $"{ok} file verrebbero rinominati, {failed} non potrebbero esserlo. Nessuna modifica effettuata: premi Avvia Ridenominazione per applicare."
            : $"{ok} file rinominati, {failed} errori, {warnings} con avvisi.";
        IsSummaryOpen = true;
        _messenger.Send(new RunCompletedMessage(ok, failed, warnings, options.DryRun, _lastJournal));
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_lastJournal is null || RootFolder is null)
        {
            return;
        }
        CanUndo = false;
        var executor = _services.GetRequiredService<RenameExecutor>();
        var errors = await Task.Run(() => executor.UndoAsync(_lastJournal, CancellationToken.None));
        foreach (var error in errors)
        {
            _messenger.Send(new FileIssueMessage(error.Detail ?? RootFolder, error, "Annulla"));
        }
        await OpenFolderCommand.ExecuteAsync(RootFolder); // ricalcola l'anteprima sullo stato reale del disco
    }

    /// <summary>Un solo pulsante "Annulla" per l'analisi e per l'esecuzione.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (OpenFolderCommand.IsRunning)
        {
            OpenFolderCancelCommand.Execute(null);
        }
        if (RunCommand.IsRunning)
        {
            RunCancelCommand.Execute(null);
        }
    }

    /// <summary>Torna alla drop-zone.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (IsBusy)
        {
            return;
        }
        Items.Clear();
        _bySource.Clear();
        _plan = [];
        RootFolder = null;
        IsSummaryOpen = false;
        Phase = AppPhase.SelectFolder;
        _messenger.Send(new RunStartedMessage("Reset"));
    }

    /// <summary>Dopo una simulazione si può tornare all'anteprima e lanciare quella vera.</summary>
    [RelayCommand]
    private Task ReanalyzeAsync() => OpenFolderCommand.ExecuteAsync(RootFolder);

    // ---- Lingua e formato al volo ---------------------------------------------------------------

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!_syncingLanguage && value is not null)
        {
            SetLanguageCommand.Execute(value.Tag);
        }
    }

    /// <summary>Cambia la lingua dei titoli e, se c'è una cartella aperta, rifà l'analisi.</summary>
    [RelayCommand]
    private async Task SetLanguageAsync(string? tag)
    {
        var settings = Settings;
        if (string.IsNullOrWhiteSpace(tag) || IsBusy || string.Equals(tag, settings.Current.Matching.Language, StringComparison.OrdinalIgnoreCase))
        {
            SyncFromSettings();
            return;
        }

        var current = settings.Current;
        await settings.SaveAsync(new RenamrSettings
        {
            Templates = current.Templates,
            Matching = new MatchingSettings
            {
                HighConfidenceThreshold = current.Matching.HighConfidenceThreshold,
                MinimumConfidence = current.Matching.MinimumConfidence,
                MaxParallelLookups = current.Matching.MaxParallelLookups,
                Language = tag,
            },
            Keys = current.Keys,
            VideoExtensions = current.VideoExtensions,
            AudioExtensions = current.AudioExtensions,
            CompanionExtensions = current.CompanionExtensions,
        });
        SyncFromSettings();
        await ReanalyzeIfOpenAsync();
    }

    /// <summary>Formato scelto dal menu contestuale: diventa il nuovo template del tipo e i nomi si ricalcolano subito.</summary>
    [RelayCommand]
    private async Task ApplyTemplatePresetAsync(TemplatePreset? preset)
    {
        if (preset is null || IsBusy)
        {
            return;
        }
        var settings = Settings;
        var current = settings.Current;
        if (current.Templates.For(preset.Kind) == preset.Pattern)
        {
            return;
        }
        await settings.SaveAsync(new RenamrSettings
        {
            Templates = current.Templates.With(preset.Kind, preset.Pattern),
            Matching = current.Matching,
            Keys = current.Keys,
            VideoExtensions = current.VideoExtensions,
            AudioExtensions = current.AudioExtensions,
            CompanionExtensions = current.CompanionExtensions,
        });
        await RefreshNamesAsync();
    }

    /// <summary>Dopo il Salva delle impostazioni: lingua cambiata = nuove ricerche, altrimenti basta ricalcolare i nomi.</summary>
    [RelayCommand]
    private async Task SettingsSavedAsync(string? previousLanguage)
    {
        SyncFromSettings();
        if (!string.Equals(previousLanguage, Settings.Current.Matching.Language, StringComparison.OrdinalIgnoreCase))
        {
            await ReanalyzeIfOpenAsync();
        }
        else
        {
            await RefreshNamesAsync();
        }
    }

    /// <summary>Template attivo per un tipo (serve al menu per mettere la spunta sulla voce corrente).</summary>
    public string TemplateFor(MediaKind kind) => Settings.Current.Templates.For(kind);

    public string CurrentLanguage => Settings.Current.Matching.Language;

    /// <summary>Come diventerebbe il nome di questa riga con un altro formato (tooltip del menu contestuale).</summary>
    public string? PreviewName(FileItemViewModel item, TemplatePreset preset)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(preset);
        if (item.Entry.Metadata is not { } metadata || item.Entry.Parsed is not { } parsed)
        {
            return null;
        }
        try
        {
            return _services.GetRequiredService<INameTemplateEngine>().Render(preset.Pattern, metadata, parsed, parsed.Extension);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void SyncFromSettings()
    {
        var current = Settings.Current;
        _syncingLanguage = true;
        try
        {
            SelectedLanguage = LanguageOption.For(current.Matching.Language);
        }
        finally
        {
            _syncingLanguage = false;
        }
        OnPropertyChanged(nameof(CurrentLanguage));

        var english = current.Matching.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        LanguageHint = english || !string.IsNullOrWhiteSpace(current.Keys.TmdbApiKey)
            ? null
            : $"Senza chiave TMDb i film non vengono riconosciuti e TVmaze, la fonte senza chiave, ha i titoli degli episodi solo in inglese: se il nome del file ne contiene già uno, Renamr tiene quello. " +
              $"Per i titoli in {SelectedLanguage.Label.ToLowerInvariant()} crea una chiave gratuita su themoviedb.org (Impostazioni ➔ API) e incollala nelle impostazioni.";
        IsLanguageHintOpen = LanguageHint is not null;
    }

    private static string? DescribeFailures(ProviderHealth? health)
    {
        if (health is null || health.Failures.Count == 0)
        {
            return null;
        }
        var lines = health.Failures.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f switch
        {
            { Key: "TMDb", Value.Code: RenamrErrorCode.ProviderAuthFailed } =>
                "TMDb ha rifiutato la chiave API, quindi i titoli sono arrivati da altre fonti (in inglese). " +
                "In Impostazioni incolla la \"Chiave API\" che trovi su themoviedb.org in Impostazioni ➔ API.",
            { Value.Code: RenamrErrorCode.ProviderAuthFailed } =>
                $"{f.Key} ha rifiutato la chiave API: i suoi risultati sono stati sostituiti da altre fonti.",
            _ => $"{f.Key}: {f.Value.Message.ToLowerInvariant()}, i suoi risultati sono stati sostituiti da altre fonti.",
        });
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ReanalyzeIfOpenAsync()
    {
        if (RootFolder is not null && !IsBusy)
        {
            await OpenFolderCommand.ExecuteAsync(RootFolder);
        }
    }

    /// <summary>Nuovi nomi con i template correnti, senza rifare le ricerche online.</summary>
    private async Task RefreshNamesAsync()
    {
        if (RootFolder is null || IsBusy || _plan.Count == 0)
        {
            return;
        }
        if (Phase == AppPhase.Completed && !LastRunWasDryRun)
        {
            // Dopo una ridenominazione vera il piano non descrive più il disco: si rianalizza.
            await ReanalyzeIfOpenAsync();
            return;
        }

        _plan = _services.GetRequiredService<RenamePlanner>().Rerender(RootFolder, _plan);
        _messenger.Send(new RunStartedMessage("Formato"));
        Items.Clear();
        _bySource.Clear();
        foreach (var entry in _plan)
        {
            AddOrUpdate(entry, publishIssue: false);
            PublishIssue(entry, "Analisi");
        }
        RecountStatuses();
        IsSummaryOpen = false;
        LastRunWasDryRun = false;
        Phase = AppPhase.Preview;
    }

    // ---- Progresso -------------------------------------------------------------------------------

    private void OnProgress(RenameProgress p)
    {
        Processed = p.Processed;
        Total = p.Total;
        ProgressPhase = p.Phase;
        CurrentFile = p.LastEntry?.SourceName;
        if (p.LastEntry is { } entry)
        {
            AddOrUpdate(entry, publishIssue: false);
        }
    }

    private void AddOrUpdate(RenamePlanEntry entry, bool publishIssue)
    {
        if (_bySource.TryGetValue(entry.SourcePath, out var item))
        {
            item.Apply(entry);
        }
        else
        {
            item = new FileItemViewModel(entry, RootFolder!);
            _bySource[entry.SourcePath] = item;
            Items.Add(item);
        }

        if (publishIssue)
        {
            PublishIssue(entry, Phase == AppPhase.Running || Phase == AppPhase.Completed ? (IsDryRun ? "Simulazione" : "Ridenominazione") : "Analisi");
        }
    }

    private void PublishIssue(RenamePlanEntry entry, string phase)
    {
        if (entry.Error is { } error)
        {
            _messenger.Send(new FileIssueMessage(entry.SourcePath, error, phase));
        }
    }

    private void RecountStatuses()
    {
        // Pronti/bassa confidenza vengono dal piano (restano validi dopo una simulazione), gli errori dalle righe.
        ReadyCount = _plan.Count(e => e.Status == PlanStatus.Ready);
        LowConfidenceCount = _plan.Count(e => e.Status == PlanStatus.LowConfidence);
        ErrorCount = Items.Count(i => i.Status == PlanStatus.Error);
    }
}
