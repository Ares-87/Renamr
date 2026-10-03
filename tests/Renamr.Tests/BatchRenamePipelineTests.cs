using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.BatchRename;
using Renamr.Core.Models;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.BatchRename;
using Renamr.Services.IO;
using Renamr.Services.Pipeline;
using Renamr.Services.Providers;

namespace Renamr.Tests;

public class BatchRenamePipelineTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    private ServiceProvider Services() => new ServiceCollection()
        .AddSingleton<ISettingsStore>(new InMemorySettingsStore())
        .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")))
        .AddRenamrServices()
        .AddSingleton<IMessenger>(new StrongReferenceMessenger())
        .AddSingleton<IFolderPickerService>(new FixedPicker(_lib.Root))
        .AddSingleton<IssuesViewModel>()
        .AddTransient<MainViewModel>()
        .BuildServiceProvider();

    private string[] FilesOnDisk() =>
        [.. Directory.EnumerateFiles(_lib.Root).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    [Fact]
    public void Scan_takes_any_file_type_and_respects_filter_and_subfolders()
    {
        _lib.File("a.jpg");
        _lib.File("b.pdf");
        _lib.File(Path.Combine("sub", "c.jpg"));
        _lib.File(".nascosto");
        var planner = new BatchRenamePlanner();

        Assert.Equal(2, planner.Scan(_lib.Root, new BatchRenameOptions()).Count);
        Assert.Equal(3, planner.Scan(_lib.Root, new BatchRenameOptions { IncludeSubfolders = true }).Count);
        Assert.Equal(2, planner.Scan(_lib.Root, new BatchRenameOptions { IncludeSubfolders = true, Filter = "jpg" }).Count);
    }

    [Fact]
    public void Plan_flags_duplicates_and_names_taken_by_files_that_stay()
    {
        _lib.File("a.txt");
        _lib.File("b.txt");
        _lib.File("keep.txt");
        var planner = new BatchRenamePlanner();
        var files = planner.Scan(_lib.Root, new BatchRenameOptions());

        // Tutti con lo stesso nome: solo il primo può averlo.
        var same = planner.Plan(_lib.Root, files, new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "uguale" }] });
        Assert.Equal(1, same.Count(e => e.Status == PlanStatus.Ready));
        Assert.Equal(2, same.Count(e => e.Status == PlanStatus.Error));

        // "a" ➔ "keep": keep.txt resta dov'è (il suo nome non cambia), quindi è un conflitto.
        var taken = planner.Plan(_lib.Root, files, new BatchRenameOptions { Rules = [new ReplaceTextRule { Find = "a", Replacement = "keep" }] });
        Assert.Equal(PlanStatus.Error, taken.Single(e => e.SourceName == "a.txt").Status);
        Assert.Equal(PlanStatus.Unchanged, taken.Single(e => e.SourceName == "keep.txt").Status);
    }

    [Fact]
    public async Task Renumbering_chains_and_swaps_work_and_can_be_undone()
    {
        _lib.File("1.txt", "uno");
        _lib.File("2.txt", "due");
        _lib.File("3.txt", "tre");
        using var sp = Services();
        var planner = sp.GetRequiredService<BatchRenamePlanner>();
        var executor = sp.GetRequiredService<BatchRenameExecutor>();
        var files = planner.Scan(_lib.Root, new BatchRenameOptions());

        // Catena: 1➔2, 2➔3, 3➔4 (ogni file prende il nome del successivo).
        var shift = new BatchRenameOptions { Rules = [new NumberingRule { Position = NumberPosition.Replace, Start = 2 }] };
        var plan = planner.Plan(_lib.Root, files, shift);
        Assert.All(plan, e => Assert.Equal(PlanStatus.Ready, e.Status));
        var results = await executor.ExecuteAsync(_lib.Root, plan, dryRun: false, null, CancellationToken.None);
        Assert.All(results, e => Assert.Equal(PlanStatus.Done, e.Status));
        Assert.Equal(["2.txt", "3.txt", "4.txt"], FilesOnDisk());
        Assert.Equal("uno", File.ReadAllText(Path.Combine(_lib.Root, "2.txt")));
        Assert.Equal("tre", File.ReadAllText(Path.Combine(_lib.Root, "4.txt")));

        // Ciclo vero: ordine inverso, 2➔4 e 4➔2 si scambiano passando da un nome temporaneo.
        files = planner.Scan(_lib.Root, new BatchRenameOptions());
        var reverse = new BatchRenameOptions { Descending = true, Rules = [new NumberingRule { Position = NumberPosition.Replace, Start = 2 }] };
        plan = planner.Plan(_lib.Root, files, reverse);
        results = await executor.ExecuteAsync(_lib.Root, plan, dryRun: false, null, CancellationToken.None);
        Assert.Equal(2, results.Count(e => e.Status == PlanStatus.Done));
        Assert.Equal(["2.txt", "3.txt", "4.txt"], FilesOnDisk());
        Assert.Equal("tre", File.ReadAllText(Path.Combine(_lib.Root, "2.txt")));
        Assert.Equal("uno", File.ReadAllText(Path.Combine(_lib.Root, "4.txt")));

        // Annulla: si torna alla situazione prima dello scambio, senza file temporanei rimasti.
        var journal = sp.GetRequiredService<RenameJournal>().CurrentFile!;
        var errors = await sp.GetRequiredService<RenameExecutor>().UndoAsync(journal, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal("uno", File.ReadAllText(Path.Combine(_lib.Root, "2.txt")));
        Assert.Equal("tre", File.ReadAllText(Path.Combine(_lib.Root, "4.txt")));
        Assert.Equal(3, Directory.EnumerateFiles(_lib.Root).Count());
    }

    [Fact]
    public async Task Dry_run_touches_nothing_and_a_file_appearing_later_is_never_overwritten()
    {
        _lib.File("a.txt", "a");
        using var sp = Services();
        var planner = sp.GetRequiredService<BatchRenamePlanner>();
        var executor = sp.GetRequiredService<BatchRenameExecutor>();
        var plan = planner.Plan(_lib.Root, planner.Scan(_lib.Root, new BatchRenameOptions()), new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "b" }] });

        var simulated = await executor.ExecuteAsync(_lib.Root, plan, dryRun: true, null, CancellationToken.None);
        Assert.Equal(PlanStatus.Simulated, simulated[0].Status);
        Assert.Equal(["a.txt"], FilesOnDisk());

        _lib.File("b.txt", "intruso");
        var results = await executor.ExecuteAsync(_lib.Root, plan, dryRun: false, null, CancellationToken.None);
        Assert.Equal(PlanStatus.Error, results[0].Status);
        Assert.Equal("intruso", File.ReadAllText(Path.Combine(_lib.Root, "b.txt")));
    }

    [Fact]
    public async Task Batch_mode_never_writes_metadata_or_dates()
    {
        var path = _lib.Fixture("sample.mkv", "The.Matrix.1999.mkv");
        var before = File.ReadAllBytes(path);
        var modified = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, modified);
        using var sp = Services();
        var planner = sp.GetRequiredService<BatchRenamePlanner>();
        var plan = planner.Plan(_lib.Root, planner.Scan(_lib.Root, new BatchRenameOptions()), new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "Film {n}" }] });

        await sp.GetRequiredService<BatchRenameExecutor>().ExecuteAsync(_lib.Root, plan, dryRun: false, null, CancellationToken.None);

        var renamed = Path.Combine(_lib.Root, "Film 1.mkv");
        Assert.Equal(before, File.ReadAllBytes(renamed));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(renamed));
    }

    [Fact]
    public void ViewModel_batch_flow_preview_rules_apply_undo() => UiThread.Run(async () =>
    {
        _lib.File("IMG_0002.JPG");
        _lib.File("IMG_0010.JPG");
        _lib.File("nota.txt");
        using var sp = Services();
        var vm = sp.GetRequiredService<MainViewModel>();
        vm.BatchPreviewDelay = TimeSpan.FromHours(1); // nei test si ricalcola a mano
        vm.IsBatchMode = true;
        Assert.Contains("Qualunque tipo di file", vm.DropZoneHint);

        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Equal(AppPhase.Preview, vm.Phase);
        Assert.Equal(3, vm.Items.Count);

        // Filtro e regole: "Vacanze 1", "Vacanze 2" in ordine naturale, estensione minuscola.
        vm.Batch.ClearRulesCommand.Execute(null);
        vm.Batch.Filter = "jpg";
        vm.Batch.AddRuleCommand.Execute("nuovoNome");
        ((NewNameRuleViewModel)vm.Batch.Rules[0]).Pattern = "Vacanze {n}";
        vm.Batch.AddRuleCommand.Execute("estensione");
        Assert.Equal("2. Estensione", vm.Batch.Rules[1].Header);
        await vm.RefreshBatchPreviewAsync(rescan: true);
        Assert.Equal(["Vacanze 1.jpg", "Vacanze 2.jpg"], vm.Items.Select(i => i.ProposedName));
        Assert.Equal("IMG_0002.JPG", vm.Items[0].OriginalName);
        Assert.Equal(2, vm.ReadyCount);

        // Modifica senza rileggere la cartella: le righe si aggiornano al loro posto.
        var firstRow = vm.Items[0];
        ((NewNameRuleViewModel)vm.Batch.Rules[0]).Pattern = "Mare {n:00}";
        await vm.RefreshBatchPreviewAsync();
        Assert.Same(firstRow, vm.Items[0]);
        Assert.Equal("Mare 01.jpg", vm.Items[0].ProposedName);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Mare 01.jpg", "Mare 02.jpg", "nota.txt"], FilesOnDisk());
        Assert.True(vm.CanUndo);

        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Equal(["IMG_0002.JPG", "IMG_0010.JPG", "nota.txt"], FilesOnDisk());
        Assert.Equal(AppPhase.Preview, vm.Phase);

        // Le regole restano salvate per il prossimo avvio, insieme alla modalità.
        await vm.RefreshBatchPreviewAsync();
        var saved = sp.GetRequiredService<BatchRenameStore>().Load();
        vm.ModeIndex = 1; // già attiva: nessun effetto
        Assert.True(saved.BatchModeActive);
        Assert.Equal("Mare {n:00}", Assert.IsType<NewNameRule>(saved.Options.Rules[0]).Pattern);
        Assert.Equal("jpg", saved.Options.Filter);
    });

    [Fact]
    public void Saving_settings_in_batch_mode_keeps_the_rule_preview() => UiThread.Run(async () =>
    {
        _lib.File("IMG_0001.jpg");
        _lib.File("The.Matrix.1999.1080p.mkv");
        using var sp = Services();
        var vm = sp.GetRequiredService<MainViewModel>();
        vm.BatchPreviewDelay = TimeSpan.FromHours(1);
        vm.IsBatchMode = true;
        await vm.PickFolderCommand.ExecuteAsync(null);
        vm.Batch.ClearRulesCommand.Execute(null);
        vm.Batch.AddRuleCommand.Execute("numerazione");
        await vm.RefreshBatchPreviewAsync();
        Assert.Equal(2, vm.ReadyCount);

        // Prima il Salva delle impostazioni ricalcolava i nomi con i template dei film anche qui.
        await vm.SettingsSavedCommand.ExecuteAsync(vm.CurrentLanguage);
        Assert.Equal(["IMG_0001 1.jpg", "The.Matrix.1999.1080p 2.mkv"], vm.Items.Select(i => i.ProposedName));
        Assert.Equal(2, vm.ReadyCount);
    });

    [Fact]
    public void Moving_and_removing_rules_renumbers_them()
    {
        var batch = new BatchRenameViewModel(new BatchRenameOptions());
        var changes = 0;
        batch.RulesChanged += (_, _) => changes++;
        batch.AddRuleCommand.Execute("numerazione");
        batch.AddRuleCommand.Execute("pulisci");
        batch.Rules[1].MoveUpCommand.Execute(null);
        Assert.Equal("1. Pulisci nome", batch.Rules[0].Header);
        Assert.False(batch.Rules[0].MoveUpCommand.CanExecute(null));
        batch.Rules[0].RemoveCommand.Execute(null);
        Assert.Equal("1. Numerazione", batch.Rules[0].Header);
        Assert.True(changes >= 4);
        Assert.IsType<NumberingRule>(Assert.Single(batch.ToOptions().Rules));
    }

    public void Dispose() => _lib.Dispose();

    private sealed class FixedPicker(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);
    }
}
