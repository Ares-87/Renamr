using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.Models;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.Providers;

namespace Renamr.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly TempLibrary _lib = new();

    private (MainViewModel Vm, ServiceProvider Sp) Create()
    {
        var services = new ServiceCollection()
            .AddSingleton<ISettingsStore>(new InMemorySettingsStore())
            .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")))
            .AddRenamrServices()
            .AddSingleton<IMetadataResolver, MatrixResolver>() // l'ultima registrazione vince: niente rete
            .AddSingleton<IMessenger>(new StrongReferenceMessenger())
            .AddSingleton<IFolderPickerService>(new FixedPicker(_lib.Root))
            .AddSingleton<IssuesViewModel>()
            .AddTransient<MainViewModel>()
            .BuildServiceProvider();
        return (services.GetRequiredService<MainViewModel>(), services);
    }

    [Fact]
    public void Full_flow_select_preview_simulate_apply_undo() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "The.Matrix.1999.1080p.mkv");
        _lib.File("Unknown.Movie.2005.mkv");
        var (vm, sp) = Create();
        using var _ = sp;

        Assert.True(vm.IsSelectPhase);

        // 1. Selezione
        await vm.PickFolderCommand.ExecuteAsync(null);
        Assert.Equal(AppPhase.Preview, vm.Phase);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(1, vm.ReadyCount);
        Assert.Equal(1, vm.ErrorCount);
        Assert.Equal(1, vm.Issues.ErrorCount); // "Nessun riscontro" arrivato via Messenger
        Assert.Equal("The Matrix (1999) [1080p].mkv", vm.Items.Single(i => i.Status == PlanStatus.Ready).ProposedName);

        // 2. Simulazione
        vm.IsDryRun = true;
        Assert.Equal("Avvia Simulazione", vm.PrimaryActionText);
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_lib.Root, "The.Matrix.1999.1080p.mkv")));
        Assert.Equal(PlanStatus.Simulated, vm.Items.Single(i => i.OriginalName.StartsWith("The.Matrix")).Status);
        Assert.True(vm.LastRunWasDryRun);
        Assert.False(vm.IsDryRun);
        Assert.True(vm.RunCommand.CanExecute(null));

        // 3. Applicazione reale dello stesso piano
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(AppPhase.Completed, vm.Phase);
        Assert.True(File.Exists(Path.Combine(_lib.Root, "The Matrix (1999) [1080p].mkv")));
        Assert.Equal("1 / 1", vm.ProgressText);
        Assert.True(vm.CanUndo);

        // Annulla
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(_lib.Root, "The.Matrix.1999.1080p.mkv")));
        Assert.Equal(AppPhase.Preview, vm.Phase);
    });

    [Fact]
    public void Reset_returns_to_drop_zone() => UiThread.Run(async () =>
    {
        _lib.Fixture("sample.mkv", "The.Matrix.1999.mkv");
        var (vm, sp) = Create();
        using var _ = sp;
        await vm.OpenFolderCommand.ExecuteAsync(_lib.Root);
        vm.ResetCommand.Execute(null);
        Assert.True(vm.IsSelectPhase);
        Assert.Empty(vm.Items);
    });

    public void Dispose() => _lib.Dispose();

    private sealed class FixedPicker(string folder) : IFolderPickerService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);
    }

    private sealed class MatrixResolver : IMetadataResolver
    {
        public Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(query.Title == "The Matrix"
                ? new MatchResult
                {
                    Outcome = MatchOutcome.Matched,
                    Best = new MatchCandidate(new MediaMetadata { Kind = MediaKind.Movie, Provider = "Fake", ProviderId = "603", Title = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31) }, 0.97),
                }
                : MatchResult.NoMatch(["Fake: nessun risultato"]));
    }
}
