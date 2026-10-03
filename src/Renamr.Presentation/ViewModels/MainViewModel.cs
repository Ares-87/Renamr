using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Presentation.Messages;
using Renamr.Presentation.Services;
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

    public MainViewModel(IServiceProvider services, IFolderPickerService folderPicker, IMessenger messenger, IssuesViewModel issues)
    {
        _services = services;
        _folderPicker = folderPicker;
        _messenger = messenger;
        Issues = issues;
    }

    public IssuesViewModel Issues { get; }

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
