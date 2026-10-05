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

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>File aggiunti a mano (anche da altre cartelle), oltre a quelli della cartella aperta.</summary>
    private readonly List<string> _addedFiles = [];

    /// <summary>File tolti dall'elenco: restano fuori anche quando si rifà l'analisi.</summary>
    private readonly HashSet<string> _removed = new(PathComparer);

    /// <summary>Solo questi file vanno analizzati alla prossima analisi (aggiunti a un elenco già pronto).</summary>
    private HashSet<string>? _analyzeOnly;

    /// <summary>Spostamenti dell'ultima ridenominazione vera, per seguire i file aggiunti a mano anche dopo "Annulla".</summary>
    private List<(string From, string To)> _lastRunMoves = [];

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
        Batch.CurrentNames = () =>
        {
            var options = Batch.ToOptions();
            return [.. BatchRenameEngine.Sort(_batchFiles, options.SortBy, options.Descending).Select(f => f.Stem)];
        };
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
    /// Spenta = "Film e serie". Passando a "Rinomina file" la cartella aperta si rilegge subito (è solo locale);
    /// tornando a "Film e serie" le ricerche online partono solo quando lo decide l'utente.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMediaMode), nameof(ModeIndex), nameof(DropZoneTitle), nameof(DropZoneHint), nameof(CanAnalyzeLastFolder))]
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
    [NotifyPropertyChangedFor(nameof(IsSelectPhase), nameof(IsPreviewVisible), nameof(IsBusy), nameof(PrimaryActionText), nameof(CanAnalyzeLastFolder))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial AppPhase Phase { get; private set; } = AppPhase.SelectFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyzeLastFolder), nameof(AnalyzeLastFolderText), nameof(SourceLabel), nameof(PrimaryFolder), nameof(HasSources))]
    public partial string? RootFolder { get; private set; }

    /// <summary>C'è qualcosa da analizzare: una cartella aperta o dei file aggiunti.</summary>
    public bool HasSources => RootFolder is not null || _addedFiles.Count > 0;

    /// <summary>Cartella di riferimento (per l'avviso sulle date su Linux): quella aperta o quella del primo file aggiunto.</summary>
    public string? PrimaryFolder => RootFolder ?? (_addedFiles.Count > 0 ? Path.GetDirectoryName(_addedFiles[0]) : null);

    /// <summary>In alto a destra: la cartella aperta, "+ N file" se se ne sono aggiunti altri, oppure "N file scelti".</summary>
    public string SourceLabel
    {
        get
        {
            var outside = _addedFiles.Count(f => RootFor(f) != RootFolder);
            return RootFolder switch
            {
                null when _addedFiles.Count == 0 => string.Empty,
                null => S.Format(nameof(Strings.SourceFiles), _addedFiles.Count),
                _ when outside > 0 => S.Format(nameof(Strings.SourceFolderPlusFiles), RootFolder, outside),
                _ => RootFolder,
            };
        }
    }

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

    /// <summary>
    /// Nella schermata iniziale, dopo un'analisi annullata o un cambio di modalità, l'ultima cartella resta a portata
    /// di un clic: l'analisi online riparte solo da qui, mai da sola.
    /// </summary>
    public bool CanAnalyzeLastFolder => IsSelectPhase && IsMediaMode && HasSources;

    public string AnalyzeLastFolderText => RootFolder is null
        ? _addedFiles.Count == 0 ? string.Empty : S.Format(nameof(Strings.AnalyzeFiles), _addedFiles.Count)
        : S.Format(nameof(Strings.AnalyzeFolder), Path.GetFileName(Path.TrimEndingDirectorySeparator(RootFolder)) is { Length: > 0 } name ? name : RootFolder);
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

    /// <summary>Usato sia dal selettore sia dal drag &amp; drop: la cartella sostituisce l'elenco di prima.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder) || IsBusy)
        {
            return;
        }

        // Normalizzazione immediata: da qui in avanti esiste solo il percorso canonico.
        RootFolder = Path.GetFullPath(folder);
        _addedFiles.Clear();
        _removed.Clear();
        _analyzeOnly = null;
        NotifySourcesChanged();
        await AnalyzeCommand.ExecuteAsync(null);
    }

    /// <summary>"Scegli file…" e "Aggiungi file…": in Film e serie solo i tipi riconosciuti, in Rinomina file qualunque file.</summary>
    [RelayCommand]
    private async Task PickFilesAsync()
    {
        if (IsBusy)
        {
            return;
        }
        var current = Settings.Current;
        IReadOnlyCollection<string>? extensions = IsBatchMode ? null : [.. current.VideoExtensions, .. current.AudioExtensions];
        var files = await _folderPicker.PickFilesAsync(extensions);
        if (files.Count > 0)
        {
            await AddFilesCommand.ExecuteAsync(files);
        }
    }

    /// <summary>
    /// Aggiunge file singoli all'elenco (dal selettore o trascinati). Dalla schermata iniziale l'elenco riparte da questi;
    /// con un'anteprima già pronta si analizzano solo i file nuovi, senza rifare le ricerche degli altri.
    /// </summary>
    [RelayCommand]
    private async Task AddFilesAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || IsBusy)
        {
            return;
        }
        if (Phase == AppPhase.SelectFolder)
        {
            RootFolder = null;
            _addedFiles.Clear();
            _removed.Clear();
            Items.Clear();
            _bySource.Clear();
            _plan = [];
            _batchFiles = [];
        }

        var settings = Settings.Current;
        var fresh = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }
            var full = Path.GetFullPath(path);
            var extension = Path.GetExtension(full);
            if (IsMediaMode && !settings.VideoExtensions.Contains(extension) && !settings.AudioExtensions.Contains(extension))
            {
                continue; // in Film e serie un documento o una foto non si possono riconoscere
            }
            _removed.Remove(full);
            if (_bySource.ContainsKey(full) || _addedFiles.Contains(full, PathComparer))
            {
                continue;
            }
            _addedFiles.Add(full);
            fresh.Add(full);
        }
        NotifySourcesChanged();
        if (fresh.Count == 0 && Phase != AppPhase.SelectFolder)
        {
            return;
        }

        var incremental = IsMediaMode && _plan.Count > 0 && (Phase == AppPhase.Preview || (Phase == AppPhase.Completed && LastRunWasDryRun));
        _analyzeOnly = incremental ? new HashSet<string>(fresh, PathComparer) : null;
        if (HasSources)
        {
            await AnalyzeCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Toglie un file dall'elenco: non si rinomina e resta fuori anche se si rifà l'analisi.</summary>
    [RelayCommand]
    private async Task RemoveItemAsync(FileItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }
        var path = item.Entry.SourcePath;
        _removed.Add(path);
        _addedFiles.RemoveAll(f => PathComparer.Equals(f, path));
        Items.Remove(item);
        _bySource.Remove(path);
        NotifySourcesChanged();

        var rest = _plan.Where(e => !PathComparer.Equals(e.SourcePath, path)).ToList();
        if (IsBatchMode)
        {
            // La numerazione e i conflitti dipendono dagli altri file: si ricalcola l'anteprima delle regole.
            _batchFiles = [.. _batchFiles.Where(f => !PathComparer.Equals(f.Path, path))];
            _plan = rest;
            if (Phase == AppPhase.Preview || (Phase == AppPhase.Completed && LastRunWasDryRun))
            {
                await RefreshBatchPreviewAsync();
                return;
            }
        }
        else
        {
            // Il nome che il file avrebbe preso si libera: un'altra riga in conflitto può tornare pronta.
            var stale = Phase == AppPhase.Completed && !LastRunWasDryRun;
            _plan = stale ? rest : _services.GetRequiredService<RenamePlanner>().Rerender(RootFolder, rest);
            if (!stale)
            {
                ShowPlan("Rimozione");
                return;
            }
        }
        RecountStatuses();
    }

    // ---- Scelta manuale della corrispondenza ----------------------------------------------------------------

    /// <summary>Si può scegliere solo in anteprima (dopo una ridenominazione vera il piano non descrive più il disco).</summary>
    public bool CanChooseMatch(FileItemViewModel? item) =>
        item is { CanChooseMatch: true } && IsMediaMode && !IsBusy && (Phase == AppPhase.Preview || (Phase == AppPhase.Completed && LastRunWasDryRun));

    /// <summary>La finestra di scelta per una riga: risultati già trovati e ricerca libera su tutti i database adatti.</summary>
    public MatchPickerViewModel CreateMatchPicker(FileItemViewModel item)
    {
        var resolver = _services.GetRequiredService<IMetadataResolver>();
        return new MatchPickerViewModel(item, (query, ct) => Task.Run(() => resolver.SearchAllAsync(query, ct), ct));
    }

    /// <summary>Il risultato scelto dall'utente diventa quello della riga, che si rinomina come una corrispondenza sicura.</summary>
    public void ApplyMatch(FileItemViewModel item, MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!CanChooseMatch(item))
        {
            return;
        }
        _plan = _services.GetRequiredService<RenamePlanner>().ApplyMatch(RootFolder, _plan, item.Entry.SourcePath, candidate);
        ShowPlan("Scelta");
    }

    /// <summary>Righe aggiornate al loro posto (la lista non salta), avvisi ripubblicati, contatori e fase di anteprima.</summary>
    private void ShowPlan(string reason)
    {
        _messenger.Send(new RunStartedMessage(reason));
        var sameRows = Items.Count == _plan.Count && Items.Select(i => i.Entry.SourcePath).SequenceEqual(_plan.Select(e => e.SourcePath), StringComparer.Ordinal);
        if (!sameRows)
        {
            Items.Clear();
            _bySource.Clear();
        }
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

    /// <summary>Cartella-recinto di un file aggiunto: la cartella aperta se ci sta dentro, altrimenti la sua.</summary>
    private string RootFor(string file)
    {
        if (RootFolder is not null)
        {
            try
            {
                if (new Renamr.Services.IO.PathBoundary(RootFolder).IsStrictDescendant(file))
                {
                    return RootFolder;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // cartella sparita: il file fa da sé
            }
        }
        return Path.GetDirectoryName(file)!;
    }

    private void NotifySourcesChanged()
    {
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(PrimaryFolder));
        OnPropertyChanged(nameof(CanAnalyzeLastFolder));
        OnPropertyChanged(nameof(AnalyzeLastFolderText));
    }

    /// <summary>Analizza l'elenco corrente: la cartella aperta, più i file aggiunti, meno quelli tolti.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken ct)
    {
        if (!HasSources || IsBusy)
        {
            return;
        }

        var only = _analyzeOnly;
        _analyzeOnly = null;
        var previousPlan = _plan;
        if (only is null)
        {
            Items.Clear();
            _bySource.Clear();
            _plan = [];
        }
        IsSummaryOpen = false;
        CanUndo = false;
        LastRunWasDryRun = false;
        _messenger.Send(new RunStartedMessage("Analisi"));
        var previousPhase = Phase;
        Phase = AppPhase.Analyzing;

        var progress = new Progress<RenameProgress>(OnProgress);
        var health = _services.GetService<ProviderHealth>();
        health?.Reset();
        ProviderWarning = null;
        IsProviderWarningOpen = false;
        _lastFailures = [];
        var root = RootFolder;
        var current = Settings.Current;
        var added = _addedFiles
            .Where(f => !_removed.Contains(f))
            // Passando da "Rinomina file" a "Film e serie" i documenti e le foto aggiunti restano fuori.
            .Where(f => IsBatchMode || current.VideoExtensions.Contains(Path.GetExtension(f)) || current.AudioExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new PlanSource(f, RootFor(f)))
            .ToList();
        var removed = new HashSet<string>(_removed, PathComparer);
        try
        {
            IReadOnlyList<RenamePlanEntry> plan;
            if (IsBatchMode)
            {
                var options = Batch.ToOptions();
                var batchPlanner = _services.GetRequiredService<BatchRenamePlanner>();
                (_batchFiles, plan) = await Task.Run(() =>
                {
                    var files = (root is null ? [] : batchPlanner.Scan(root, options)).Where(f => !removed.Contains(f.Path)).ToList();
                    var known = new HashSet<string>(files.Select(f => f.Path), PathComparer);
                    files.AddRange(added.Where(a => known.Add(a.Path)).Select(a => BatchRenamePlanner.Describe(a.Path, a.Root)).OfType<BatchFile>());
                    var withDetails = BatchRenamePlanner.WithDetails(files, options);
                    return (withDetails, batchPlanner.Plan(root, withDetails, options));
                }, ct);
            }
            else
            {
                var planner = _services.GetRequiredService<RenamePlanner>();
                var scanner = _services.GetRequiredService<MediaScanner>();
                var sources = await Task.Run(() =>
                {
                    var list = root is null
                        ? []
                        : scanner.Scan(new Renamr.Services.IO.PathBoundary(root)).Where(f => !removed.Contains(f)).Select(f => new PlanSource(f, root)).ToList();
                    var known = new HashSet<string>(list.Select(f => f.Path), PathComparer);
                    list.AddRange(added.Where(a => known.Add(a.Path)));
                    return only is null ? list : list.Where(f => only.Contains(f.Path)).ToList();
                }, ct);
                plan = await Task.Run(() => planner.PlanFilesAsync(sources, progress, ct), ct);
                if (only is not null)
                {
                    // Le righe di prima restano com'erano (anche le scelte fatte a mano); si ricontrollano solo i conflitti.
                    plan = planner.Rerender(root, [.. previousPlan, .. plan]);
                }
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
            if (only is not null)
            {
                // Annullata l'aggiunta: l'elenco di prima resta com'era.
                _plan = previousPlan;
                ShowPlan("Analisi");
                return;
            }
            Phase = Items.Count > 0 ? AppPhase.Preview : AppPhase.SelectFolder;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            _messenger.Send(new FileIssueMessage(root ?? added.FirstOrDefault()?.Path ?? string.Empty, RenamrError.From(RenamrErrorCode.AccessDenied, ex.Message), "Analisi"));
            Phase = only is not null && previousPhase != AppPhase.SelectFolder ? previousPhase : AppPhase.SelectFolder;
        }
    }

    // ---- Fase 3: azione unica -----------------------------------------------------------------

    private bool CanRun() => (Phase is AppPhase.Preview || (Phase is AppPhase.Completed && LastRunWasDryRun)) && ActionableCount > 0;

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        var root = RootFolder;
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

        if (!options.DryRun)
        {
            // I file aggiunti a mano seguono il loro nuovo nome: rifacendo l'analisi restano nell'elenco.
            _lastRunMoves = [.. results.Where(r => r is { Status: PlanStatus.Done, TargetPath: not null }).Select(r => (r.SourcePath, r.TargetPath!))];
            FollowAddedFiles(_lastRunMoves);
        }

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
        if (_lastJournal is null || !HasSources)
        {
            return;
        }
        CanUndo = false;
        var executor = _services.GetRequiredService<RenameExecutor>();
        var errors = await Task.Run(() => executor.UndoAsync(_lastJournal, CancellationToken.None));
        foreach (var error in errors)
        {
            _messenger.Send(new FileIssueMessage(error.Detail ?? PrimaryFolder ?? string.Empty, error, "Annulla"));
        }
        FollowAddedFiles(_lastRunMoves.Select(m => (m.To, m.From)));
        _lastRunMoves = [];
        await AnalyzeCommand.ExecuteAsync(null); // ricalcola l'anteprima sullo stato reale del disco
    }

    private void FollowAddedFiles(IEnumerable<(string From, string To)> moves)
    {
        foreach (var (from, to) in moves)
        {
            var i = _addedFiles.FindIndex(f => PathComparer.Equals(f, from));
            if (i >= 0)
            {
                _addedFiles[i] = to;
            }
        }
    }

    /// <summary>Un solo pulsante "Annulla" per l'analisi e per l'esecuzione.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (AnalyzeCommand.IsRunning)
        {
            AnalyzeCancelCommand.Execute(null);
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
        _addedFiles.Clear();
        _removed.Clear();
        _lastRunMoves = [];
        NotifySourcesChanged();
        IsSummaryOpen = false;
        Phase = AppPhase.SelectFolder;
        _messenger.Send(new RunStartedMessage("Reset"));
    }

    /// <summary>Dopo una simulazione si può tornare all'anteprima e lanciare quella vera.</summary>
    [RelayCommand]
    private Task ReanalyzeAsync() => AnalyzeCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task AnalyzeLastFolderAsync() => AnalyzeCommand.ExecuteAsync(null);

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
        OnPropertyChanged(nameof(AnalyzeLastFolderText));
        OnPropertyChanged(nameof(SourceLabel));
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

    /// <summary>Rifà le ricerche solo se c'è un'analisi a schermo: nella schermata iniziale decide l'utente.</summary>
    private async Task ReanalyzeIfOpenAsync()
    {
        if (HasSources && !IsBusy && Phase != AppPhase.SelectFolder)
        {
            await AnalyzeCommand.ExecuteAsync(null);
        }
    }

    /// <summary>Nuovi nomi con i template correnti, senza rifare le ricerche online.</summary>
    private async Task RefreshNamesAsync()
    {
        if (!HasSources || IsBusy || _plan.Count == 0)
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
        ShowPlan("Formato");
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
        if (!HasSources || IsBusy)
        {
            return;
        }
        if (value)
        {
            AnalyzeCommand.Execute(null); // solo lettura locale della cartella: immediata
            return;
        }

        // Verso "Film e serie": le ricerche online non partono da sole. L'anteprima delle regole non vale qui,
        // quindi si torna alla schermata iniziale con il pulsante per analizzare l'ultima cartella.
        Items.Clear();
        _bySource.Clear();
        _plan = [];
        _batchFiles = [];
        RecountStatuses();
        IsSummaryOpen = false;
        LastRunWasDryRun = false;
        Phase = AppPhase.SelectFolder;
        _messenger.Send(new RunStartedMessage("Reset"));
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
        if (!IsBatchMode || !HasSources || Phase == AppPhase.SelectFolder)
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
            await AnalyzeCommand.ExecuteAsync(null);
            return;
        }

        var options = Batch.ToOptions();
        if (BatchTokens.NeedsDetails(options.Rules) && _batchFiles.Any(f => f.Details is null))
        {
            // Una regola ora usa {scatto}, {artista}…: si legge il contenuto dei file, una volta sola.
            var files = _batchFiles;
            _batchFiles = await Task.Run(() => BatchRenamePlanner.WithDetails(files, options));
        }
        _plan = _services.GetRequiredService<BatchRenamePlanner>().Plan(RootFolder, _batchFiles, options);
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
            item = new FileItemViewModel(entry, RootFolder);
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
