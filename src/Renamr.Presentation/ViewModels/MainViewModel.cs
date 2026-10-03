using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.BatchRename;
using Renamr.Core.Errors;
using Renamr.Core.Localization;
using Renamr.Core.Models;
using Renamr.Core.Options;
using Renamr.Core.Templating;
using Renamr.Presentation.Messages;
using Renamr.Presentation.Services;
using Renamr.Services.BatchRename;
using Renamr.Services.Matching;
using Renamr.Services.Pipeline;

namespace Renamr.Presentation.ViewModels;

/// <summary>
/// ViewModel della finestra principale: governa il flusso Selezione ➔ Anteprima ➔ Azione.
/// Due modalità con lo stesso flusso: "Film e serie" (riconoscimento online, metadati e date) e
/// "Rinomina file" (qualunque file, regole dell'utente, solo il nome).
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
    private bool _syncingFromSettings;
    private IReadOnlyList<BatchFile> _batchFiles = [];
    private int _batchRefreshVersion;
    private bool _pendingRescan;
    private (int Ok, int Failed, int Warnings, bool DryRun)? _lastSummary;
    private KeyValuePair<string, RenamrError>[] _lastFailures = [];

    public MainViewModel(IServiceProvider services, IFolderPickerService folderPicker, IMessenger messenger, IssuesViewModel issues)
    {
        _services = services;
        _folderPicker = folderPicker;
        _messenger = messenger;
        Issues = issues;

        var batchState = services.GetRequiredService<BatchRenameStore>().Load();
        Batch = new BatchRenameViewModel(batchState.Options);
        Batch.RulesChanged += (_, _) => ScheduleBatchRefresh(rescan: false);
        Batch.ScopeChanged += (_, _) => ScheduleBatchRefresh(rescan: true);
        _syncingFromSettings = true;
        IsBatchMode = batchState.BatchModeActive;
        _syncingFromSettings = false;

        SyncFromSettings();
        Strings.Current.PropertyChanged += (_, _) => RefreshTexts();
    }

    private static Strings S => Strings.Current;

    public IssuesViewModel Issues { get; }

    /// <summary>Regole, filtro e ordine della modalità "Rinomina file".</summary>
    public BatchRenameViewModel Batch { get; }

    /// <summary>Attesa dopo l'ultimo tasto prima di ricalcolare l'anteprima delle regole.</summary>
    public TimeSpan BatchPreviewDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Modalità "Rinomina file": qualunque file, nomi decisi dalle regole, nessun metadato e nessuna data toccata.
    /// Spenta = "Film e serie". Cambiarla con una cartella aperta rifà l'analisi nell'altra modalità.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaMode), nameof(ModeIndex), nameof(DropZoneTitle), nameof(DropZoneHint))]
    public partial bool IsBatchMode { get; set; }

    public bool IsMediaMode => !IsBatchMode;

    /// <summary>Per il selettore in alto: 0 = Film e serie, 1 = Rinomina file.</summary>
    public int ModeIndex
    {
        get => IsBatchMode ? 1 : 0;
        set => IsBatchMode = value == 1;
    }

    public string DropZoneTitle => IsBatchMode ? S.DropTitleBatch : S.DropTitleMedia;

    public string DropZoneHint => IsBatchMode ? S.DropHintBatch : S.DropHintMedia;

    /// <summary>"Renamr v1.1.0": accanto al nome, per capire al volo se si sta usando l'ultima revisione.</summary>
    public string AppTitle => AppInfo.Title;

    /// <summary>"v1.8.0", accanto al logo.</summary>
    public string AppVersion => $"v{AppInfo.Version}";

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

    /// <summary>Scrivere titolo e data anche dentro i file (tag). Spento = solo nome e date del file system.</summary>
    [ObservableProperty]
    public partial bool WriteEmbeddedMetadata { get; set; } = true;

    /// <summary>La data di creazione è tra le date da cambiare (Impostazioni ➔ Metadati da scrivere).</summary>
    public bool SetsCreationDate => Settings.Current.Output.FileDates.Creation;

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
    public string PrimaryActionText => IsDryRun ? S.StartDryRun : S.StartRename;

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
        _lastFailures = [];
        try
        {
            IReadOnlyList<RenamePlanEntry> plan;
            if (IsBatchMode)
            {
                var root = RootFolder;
                var options = Batch.ToOptions();
                var batchPlanner = _services.GetRequiredService<BatchRenamePlanner>();
                (_batchFiles, plan) = await Task.Run(() =>
                {
                    var files = batchPlanner.Scan(root, options);
                    return (files, batchPlanner.Plan(root, files, options));
                }, ct);
            }
            else
            {
                var planner = _services.GetRequiredService<RenamePlanner>();
                plan = await Task.Run(() => planner.PlanAsync(RootFolder, progress, ct), ct);
            }
            // Il planner chiude senza eccezioni quando si annulla: il piano parziale va scartato qui.
            ct.ThrowIfCancellationRequested();
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
            _lastFailures = health is null ? [] : [.. health.Failures];
            ProviderWarning = DescribeFailures(_lastFailures);
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
        var output = Settings.Current.Output;
        var options = new RenameRunOptions
        {
            DryRun = IsDryRun,
            IncludeLowConfidence = IncludeLowConfidence,
            WriteEmbeddedMetadata = WriteEmbeddedMetadata,
            EmbeddedFields = output.EmbeddedFields,
            FileDates = output.FileDates,
        };
        var plan = _plan;

        _messenger.Send(new RunStartedMessage(IsDryRun ? "Simulazione" : "Ridenominazione"));
        Phase = AppPhase.Running;

        var progress = new Progress<RenameProgress>(OnProgress);
        IReadOnlyList<RenamePlanEntry> results;
        try
        {
            if (IsBatchMode)
            {
                // Solo il nome: nessun metadato interno e nessuna data, qualunque cosa dicano le opzioni dei film.
                var batch = _services.GetRequiredService<BatchRenameExecutor>();
                results = await Task.Run(() => batch.ExecuteAsync(root, plan, options.DryRun, progress, ct), CancellationToken.None);
            }
            else
            {
                var executor = _services.GetRequiredService<RenameExecutor>();
                results = await Task.Run(() => executor.ExecuteAsync(root, plan, options, progress, ct), CancellationToken.None);
            }
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
        _lastSummary = (ok, failed, warnings, options.DryRun);
        DescribeSummary();
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
        _batchFiles = [];
        RootFolder = null;
        IsSummaryOpen = false;
        Phase = AppPhase.SelectFolder;
        _messenger.Send(new RunStartedMessage("Reset"));
    }

    /// <summary>Dopo una simulazione si può tornare all'anteprima e lanciare quella vera.</summary>
    [RelayCommand]
    private Task ReanalyzeAsync() => OpenFolderCommand.ExecuteAsync(RootFolder);

    // ---- Lingua e formato al volo ---------------------------------------------------------------

    partial void OnWriteEmbeddedMetadataChanged(bool value)
    {
        if (!_syncingFromSettings)
        {
            SaveWriteEmbeddedMetadataCommand.Execute(value);
        }
    }

    /// <summary>La scelta "Scrivi metadati interni" resta per le prossime volte.</summary>
    [RelayCommand]
    private Task SaveWriteEmbeddedMetadataAsync(bool value)
    {
        var settings = Settings;
        var current = settings.Current;
        if (current.Output.WriteEmbeddedMetadata == value)
        {
            return Task.CompletedTask;
        }
        return settings.SaveAsync(new RenamrSettings
        {
            Templates = current.Templates,
            Matching = current.Matching,
            Keys = current.Keys,
            Output = current.Output.WithWriteEmbeddedMetadata(value),
            VideoExtensions = current.VideoExtensions,
            AudioExtensions = current.AudioExtensions,
            CompanionExtensions = current.CompanionExtensions,
        });
    }

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (!_syncingFromSettings && value is not null)
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
            Output = current.Output,
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
            Output = current.Output,
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
        if (IsBatchMode)
        {
            // I nomi vengono dalle regole: niente ricerche né template dei film (Rerender qui produceva un piano "film").
            // Si ricalcola solo l'anteprima, così anche i messaggi seguono un'eventuale nuova lingua.
            await RefreshBatchPreviewAsync();
            return;
        }
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
        _syncingFromSettings = true;
        try
        {
            SelectedLanguage = LanguageOption.For(current.Matching.Language);
            WriteEmbeddedMetadata = current.Output.WriteEmbeddedMetadata;
        }
        finally
        {
            _syncingFromSettings = false;
        }
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(SetsCreationDate));

        var english = current.Matching.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        LanguageHint = english || !string.IsNullOrWhiteSpace(current.Keys.TmdbApiKey)
            ? null
            : S.Format(nameof(Strings.LanguageHintNoTmdb), SelectedLanguage.Label);
        IsLanguageHintOpen = LanguageHint is not null && !IsBatchMode;
    }

    private static string? DescribeFailures(KeyValuePair<string, RenamrError>[] failures)
    {
        if (failures.Length == 0)
        {
            return null;
        }
        var lines = failures.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Select(f => f switch
        {
            { Key: "TMDb", Value.Code: RenamrErrorCode.ProviderAuthFailed } => S.ProviderTmdbRejected,
            { Value.Code: RenamrErrorCode.ProviderAuthFailed } => S.Format(nameof(Strings.ProviderKeyRejected), f.Key),
            _ => S.Format(nameof(Strings.ProviderFailed), f.Key, ErrorMessages.Describe(f.Value.Code).ToLowerInvariant()),
        });
        return string.Join(Environment.NewLine, lines);
    }

    private void DescribeSummary()
    {
        if (_lastSummary is not { } r)
        {
            return;
        }
        SummaryTitle = r.DryRun ? S.SummaryDryRunTitle : S.SummaryRenameTitle;
        SummaryMessage = r.DryRun
            ? S.Format(nameof(Strings.SummaryDryRunMessage), r.Ok, r.Failed)
            : S.Format(nameof(Strings.SummaryRenameMessage), r.Ok, r.Failed, r.Warnings);
    }

    /// <summary>
    /// Lingua dell'interfaccia cambiata: si rifanno i testi calcolati qui (riepilogo, avvisi, stati delle righe, regole).
    /// I messaggi d'errore già arrivati dall'analisi restano com'erano fino alla prossima analisi.
    /// </summary>
    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(DropZoneTitle));
        OnPropertyChanged(nameof(DropZoneHint));
        OnPropertyChanged(nameof(PrimaryActionText));
        SyncFromSettings();
        DescribeSummary();
        ProviderWarning = DescribeFailures(_lastFailures);
        foreach (var item in Items)
        {
            item.RefreshTexts();
        }
        Batch.RefreshTexts();
        Issues.RefreshTexts();
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

    // ---- Modalità "Rinomina file" ------------------------------------------------------------------

    partial void OnIsBatchModeChanged(bool value)
    {
        if (_syncingFromSettings)
        {
            return;
        }
        IsLanguageHintOpen = LanguageHint is not null && !value;
        IsProviderWarningOpen = false;
        SaveBatchState();
        if (RootFolder is not null && !IsBusy)
        {
            OpenFolderCommand.Execute(RootFolder);
        }
    }

    /// <summary>Le regole cambiano a ogni tasto: si aspetta una pausa breve e si ricalcola una volta sola.</summary>
    private void ScheduleBatchRefresh(bool rescan)
    {
        _pendingRescan |= rescan;
        _ = DebouncedBatchRefreshAsync(++_batchRefreshVersion);
    }

    private async Task DebouncedBatchRefreshAsync(int version)
    {
        await Task.Delay(BatchPreviewDelay);
        if (version != _batchRefreshVersion)
        {
            return; // è arrivato un altro tasto: ci pensa la chiamata più recente
        }
        var rescan = _pendingRescan;
        _pendingRescan = false;
        await RefreshBatchPreviewAsync(rescan);
    }

    /// <summary>
    /// Ricalcola l'anteprima con le regole correnti. Senza <paramref name="rescan"/> usa l'elenco di file già letto;
    /// con filtro o sottocartelle cambiati, o dopo una ridenominazione vera, rilegge la cartella.
    /// </summary>
    public async Task RefreshBatchPreviewAsync(bool rescan = false)
    {
        SaveBatchState();
        if (!IsBatchMode || RootFolder is null || Phase == AppPhase.SelectFolder)
        {
            return;
        }
        if (IsBusy)
        {
            ScheduleBatchRefresh(rescan); // a fine analisi
            return;
        }
        if (rescan || (Phase == AppPhase.Completed && !LastRunWasDryRun))
        {
            await OpenFolderCommand.ExecuteAsync(RootFolder);
            return;
        }

        _plan = _services.GetRequiredService<BatchRenamePlanner>().Plan(RootFolder, _batchFiles, Batch.ToOptions());
        _messenger.Send(new RunStartedMessage("Regole"));
        var sameRows = Items.Count == _plan.Count && Items.Select(i => i.Entry.SourcePath).SequenceEqual(_plan.Select(e => e.SourcePath), StringComparer.Ordinal);
        if (!sameRows)
        {
            // Ordine cambiato: si ricostruisce. Altrimenti si aggiornano le righe al loro posto (la lista non salta).
            Items.Clear();
            _bySource.Clear();
        }
        foreach (var entry in _plan)
        {
            AddOrUpdate(entry, publishIssue: false);
            PublishIssue(entry, "Anteprima");
        }
        RecountStatuses();
        IsSummaryOpen = false;
        LastRunWasDryRun = false;
        Phase = AppPhase.Preview;
    }

    private void SaveBatchState() =>
        _services.GetRequiredService<BatchRenameStore>().Save(new BatchRenameState { BatchModeActive = IsBatchMode, Options = Batch.ToOptions() });

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
