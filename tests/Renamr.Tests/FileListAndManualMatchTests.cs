using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.Providers;

namespace Renamr.Tests;

/// <summary>
/// Scelta a mano della corrispondenza, file aggiunti singolarmente (anche da altre cartelle), file tolti dall'elenco
/// e righe "Già corretto" che restano fuori dalla ridenominazione.
/// </summary>
public class FileListAndManualMatchTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    private string Library => Path.Combine(_lib.Root, "Libreria");

    private (MainViewModel Vm, ServiceProvider Sp, FilesPicker Picker) Create()
    {
        Directory.CreateDirectory(Library);
        var picker = new FilesPicker(Library);
        var services = new ServiceCollection()
            .AddSingleton<ISettingsStore>(new InMemorySettingsStore())
            .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")))
            .AddRenamrServices()
            .AddSingleton<IMetadataResolver, FakeResolver>() // l'ultima registrazione vince: niente rete
            .AddSingleton<IMessenger>(new StrongReferenceMessenger())
            .AddSingleton<IFolderPickerService>(picker)
            .AddSingleton<IssuesViewModel>()
            .AddTransient<MainViewModel>()
            .BuildServiceProvider();
        return (services.GetRequiredService<MainViewModel>(), services, picker);
    }

    [Fact]
    public void Low_confidence_match_chosen_by_hand_is_renamed_without_include_low_confidence() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "Libreria/Inceptio.2010.mkv");
        var (vm, sp, _) = Create();
        using var _ = sp;
        await vm.PickFolderCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Items);
        Assert.Equal(PlanStatus.LowConfidence, row.Status);
        Assert.True(row.SuggestsChoice);
        Assert.True(vm.CanChooseMatch(row));

        // I risultati trovati dall'analisi sono già nella finestra, quello in uso per primo selezionato.
        var picker = vm.CreateMatchPicker(row);
        Assert.Equal(["Inception (2010)", "Inception: The Cobol Job (2010)"], picker.Choices.Select(c => c.Title));
        Assert.True(picker.Choices[0].IsCurrent);

        vm.ApplyMatch(row, picker.Choices[0].Candidate);

        row = Assert.Single(vm.Items);
        Assert.Equal(PlanStatus.Ready, row.Status);
        Assert.Contains("scelto da te", row.MatchSummary);
        Assert.Equal(1, vm.ReadyCount);
        Assert.Equal(0, vm.LowConfidenceCount);
        Assert.False(vm.IncludeLowConfidence);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(Library, "Inception (2010).mkv")));
    });

    [Fact]
    public void Unmatched_file_can_be_paired_with_a_custom_search() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "Libreria/Il.Film.Sconosciuto.2005.mkv");
        var (vm, sp, _) = Create();
        using var _ = sp;
        await vm.PickFolderCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Items);
        Assert.Equal(PlanStatus.Error, row.Status);
        Assert.True(row.SuggestsChoice);

        // L'analisi ha scartato un risultato troppo incerto: resta comunque da scegliere.
        var picker = vm.CreateMatchPicker(row);
        Assert.Equal("Il Film Sconosciuto", picker.Query);
        Assert.Equal("2005", picker.Year);
        Assert.Equal(["Un altro film (1990)"], picker.Choices.Select(c => c.Title));

        picker.Query = "The Unknown Movie";
        await picker.SearchCommand.ExecuteAsync(null);
        var found = Assert.Single(picker.Choices);
        Assert.Equal("The Unknown Movie (2005)", found.Title);
        picker.SelectedChoice = found;
        Assert.True(picker.CanApply);

        vm.ApplyMatch(row, found.Candidate);
        Assert.Equal(PlanStatus.Ready, vm.Items.Single().Status);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(Library, "The Unknown Movie (2005).mkv")));
    });

    [Fact]
    public void Already_correct_files_stay_out_of_the_run_and_of_the_progress() => UiThread.Run(async () =>
    {
        var correct = _lib.File("Libreria/The Matrix (1999).mkv");
        var touched = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(correct, touched);
        _lib.Fixture("sample.mkv", "Libreria/Inception.2010.mkv");
        var (vm, sp, _) = Create();
        using var _ = sp;
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Equal(PlanStatus.Unchanged, vm.Items.Single(i => i.OriginalName.StartsWith("The Matrix")).Status);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("1 / 1", vm.ProgressText);
        Assert.Equal(PlanStatus.Unchanged, vm.Items.Single(i => i.OriginalName.StartsWith("The Matrix")).Status);
        Assert.Equal(touched, File.GetLastWriteTimeUtc(correct)); // nessuna data riscritta
        Assert.True(File.Exists(Path.Combine(Library, "Inception (2010).mkv")));
    });

    [Fact]
    public void Single_files_from_another_folder_join_the_list_and_are_renamed_where_they_are() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "Libreria/Inception.2010.mkv");
        var outside = _lib.Fixture("sample.mkv", "Altrove/The.Matrix.1999.mkv");
        var (vm, sp, picker) = Create();
        using var _ = sp;
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Single(vm.Items);

        picker.Files = [outside, _lib.File("Altrove/nota.txt")]; // in Film e serie un documento non entra
        await vm.PickFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Items.Count);
        var added = vm.Items.Single(i => i.OriginalName.StartsWith("The.Matrix"));
        Assert.Equal(Path.Combine(_lib.Root, "Altrove"), added.RelativeFolder);
        Assert.Equal($"{Library} + 1 file", vm.SourceLabel);
        Assert.Equal(2, vm.ReadyCount);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_lib.Root, "Altrove", "The Matrix (1999).mkv")));
        Assert.True(File.Exists(Path.Combine(Library, "Inception (2010).mkv")));

        // "Annulla" riporta anche il file dell'altra cartella, che resta nell'elenco col suo nome di prima.
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(File.Exists(outside));
        Assert.Contains(vm.Items, i => i.OriginalName == "The.Matrix.1999.mkv");
    });

    [Fact]
    public void Files_alone_make_a_list_without_any_folder() => UiThread.Run(async () =>
    {
        var a = _lib.Fixture("sample.mkv", "Uno/The.Matrix.1999.mkv");
        var b = _lib.Fixture("sample.mkv", "Due/Inception.2010.mkv");
        var (vm, sp, _) = Create();
        using var _ = sp;

        await vm.AddFilesCommand.ExecuteAsync(new[] { a, b });

        Assert.Equal(AppPhase.Preview, vm.Phase);
        Assert.Null(vm.RootFolder);
        Assert.Equal("2 file scelti", vm.SourceLabel);
        Assert.Equal(2, vm.ReadyCount);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_lib.Root, "Uno", "The Matrix (1999).mkv")));
        Assert.True(File.Exists(Path.Combine(_lib.Root, "Due", "Inception (2010).mkv")));
    });

    [Fact]
    public void Removed_files_are_not_renamed_and_stay_out_after_a_new_analysis() => UiThread.Run(async () =>
    {
        var keep = _lib.Fixture("sample.mkv", "Libreria/The.Matrix.1999.mkv");
        _lib.Fixture("sample.mkv", "Libreria/Inception.2010.mkv");
        var (vm, sp, _) = Create();
        using var _ = sp;
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ReadyCount);

        await vm.RemoveItemCommand.ExecuteAsync(vm.Items.Single(i => i.OriginalName.StartsWith("The.Matrix")));
        Assert.Single(vm.Items);
        Assert.Equal(1, vm.ReadyCount);

        await vm.ReanalyzeCommand.ExecuteAsync(null);
        Assert.Single(vm.Items);

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(Path.Combine(Library, "Inception (2010).mkv")));
    });

    [Fact]
    public void Removing_a_file_in_batch_mode_renumbers_the_others() => UiThread.Run(async () =>
    {
        _lib.File("Libreria/a.jpg");
        _lib.File("Libreria/b.jpg");
        _lib.File("Libreria/c.jpg");
        var (vm, sp, _) = Create();
        using var _ = sp;
        vm.BatchPreviewDelay = TimeSpan.FromHours(1);
        vm.IsBatchMode = true;
        vm.Batch.ClearRulesCommand.Execute(null);
        vm.Batch.AddRuleCommand.Execute("nuovoNome");
        ((NewNameRuleViewModel)vm.Batch.Rules[0]).Pattern = "Foto {n}";
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Equal(["Foto 1.jpg", "Foto 2.jpg", "Foto 3.jpg"], vm.Items.Select(i => i.ProposedName));

        await vm.RemoveItemCommand.ExecuteAsync(vm.Items.Single(i => i.OriginalName == "b.jpg"));

        Assert.Equal(["a.jpg", "c.jpg"], vm.Items.Select(i => i.OriginalName));
        Assert.Equal(["Foto 1.jpg", "Foto 2.jpg"], vm.Items.Select(i => i.ProposedName));
    });

    public void Dispose() => _lib.Dispose();

    private sealed class FilesPicker(string folder) : IFolderPickerService
    {
        public IReadOnlyList<string> Files { get; set; } = [];

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);

        public Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyCollection<string>? extensions) => Task.FromResult(Files);
    }

    private sealed class FakeResolver : IMetadataResolver
    {
        private static MediaMetadata Movie(string id, string title, int year) =>
            new() { Kind = MediaKind.Movie, Provider = "Fake", ProviderId = id, Title = title, ReleaseDate = new DateOnly(year, 6, 1) };

        private static readonly MatchCandidate Matrix = new(Movie("603", "The Matrix", 1999), 0.97);
        private static readonly MatchCandidate Inception = new(Movie("27205", "Inception", 2010), 0.97);

        public Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken) => Task.FromResult(query.Title switch
        {
            "The Matrix" => new MatchResult { Outcome = MatchOutcome.Matched, Best = Matrix },
            "Inception" => new MatchResult { Outcome = MatchOutcome.Matched, Best = Inception },
            "Inceptio" => new MatchResult
            {
                Outcome = MatchOutcome.LowConfidence,
                Best = Inception with { Confidence = 0.7 },
                Alternatives = [new MatchCandidate(Movie("1", "Inception: The Cobol Job", 2010), 0.5)],
            },
            _ => new MatchResult
            {
                Outcome = MatchOutcome.NoMatch,
                Alternatives = [new MatchCandidate(Movie("2", "Un altro film", 1990), 0.3)],
            },
        });

        public Task<MatchResult> SearchAllAsync(MediaQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(query.Title == "The Unknown Movie"
                ? new MatchResult { Outcome = MatchOutcome.Matched, Alternatives = [new MatchCandidate(Movie("9", "The Unknown Movie", 2005), 0.9)] }
                : new MatchResult { Outcome = MatchOutcome.NoMatch });
    }
}
