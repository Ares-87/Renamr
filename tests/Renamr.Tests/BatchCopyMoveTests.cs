using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Renamr.Core.Abstractions;
using Renamr.Core.BatchRename;
using Renamr.Core.Models;
using Renamr.Services;
using Renamr.Services.BatchRename;
using Renamr.Services.Pipeline;
using Renamr.Services.Providers;

namespace Renamr.Tests;

/// <summary>v1.17: copia e sposta in un'altra cartella, "(2)" sui nomi già presi, rinomina delle cartelle. Tutto annullabile.</summary>
public class BatchCopyMoveTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly TempLibrary _out = new();

    private ServiceProvider Services() => new ServiceCollection()
        .AddSingleton<ISettingsStore>(new InMemorySettingsStore())
        .AddSingleton<IAppPaths>(new DefaultAppPaths(Path.Combine(_out.Root, "..", Path.GetFileName(_out.Root) + "-appdata")))
        .AddRenamrServices()
        .AddSingleton<IMessenger>(new StrongReferenceMessenger())
        .BuildServiceProvider();

    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    private async Task<(IReadOnlyList<RenamePlanEntry> Result, string? Journal)> Run(ServiceProvider sp, BatchRenameOptions options)
    {
        var planner = sp.GetRequiredService<BatchRenamePlanner>();
        var plan = planner.Plan(_lib.Root, planner.Scan(_lib.Root, options), options);
        var result = await sp.GetRequiredService<BatchRenameExecutor>().ExecuteAsync(_lib.Root, plan, dryRun: false, null, CancellationToken.None);
        return (result, sp.GetRequiredService<RenameJournal>().CurrentFile);
    }

    [Fact]
    public async Task Copy_into_dated_subfolders_leaves_the_originals_and_undo_removes_the_copies()
    {
        _lib.File("a.txt", "uno");
        _lib.File("b.txt", "due");
        File.SetLastWriteTimeUtc(Path.Combine(_lib.Root, "a.txt"), new DateTime(2021, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(_lib.Root, "b.txt"), new DateTime(2022, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        using var sp = Services();
        var options = new BatchRenameOptions
        {
            Rules = [new ChangeCaseRule { Mode = CaseMode.Upper }],
            Action = BatchAction.Copy,
            DestinationFolder = _out.Root,
            SubfolderPattern = "Foto/{data:yyyy}",
        };

        var (result, journal) = await Run(sp, options);
        Assert.All(result, e => Assert.Equal(PlanStatus.Done, e.Status));
        Assert.Equal(["a.txt", "b.txt"], Tree(_lib.Root));
        Assert.Equal(["Foto", "Foto/2021", "Foto/2021/A.txt", "Foto/2022", "Foto/2022/B.txt"], Tree(_out.Root));
        Assert.Equal("uno", File.ReadAllText(Path.Combine(_out.Root, "Foto", "2021", "A.txt")));
        Assert.Equal(new DateTime(2021, 5, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_out.Root, "Foto", "2021", "A.txt")));

        var errors = await sp.GetRequiredService<RenameExecutor>().UndoAsync(journal!, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Empty(Tree(_out.Root));
        Assert.Equal(["a.txt", "b.txt"], Tree(_lib.Root));
    }

    [Fact]
    public async Task Move_into_subfolders_of_the_open_folder_and_undo()
    {
        _lib.File("Queen - Innuendo.mp3");
        _lib.File("Queen - Jazz.mp3");
        _lib.File("ABBA - Arrival.mp3");
        using var sp = Services();
        var options = new BatchRenameOptions
        {
            Rules = [new SwapPartsRule()],
            Action = BatchAction.Move,
            SubfolderPattern = "{parola:-1}", // l'ultima parola dopo lo scambio = l'artista
        };

        var (result, journal) = await Run(sp, options);
        Assert.All(result, e => Assert.Equal(PlanStatus.Done, e.Status));
        Assert.Equal(["ABBA", "ABBA/Arrival - ABBA.mp3", "Queen", "Queen/Innuendo - Queen.mp3", "Queen/Jazz - Queen.mp3"], Tree(_lib.Root));

        Assert.Empty(await sp.GetRequiredService<RenameExecutor>().UndoAsync(journal!, CancellationToken.None));
        Assert.Equal(["ABBA - Arrival.mp3", "Queen - Innuendo.mp3", "Queen - Jazz.mp3"], Tree(_lib.Root));
    }

    [Fact]
    public void Taken_names_get_a_number_when_asked()
    {
        _lib.File("a.txt");
        _lib.File("b.txt");
        _lib.File("c.txt");
        _lib.File("Foto.txt"); // resta dov'è: occupa "Foto"
        var planner = new BatchRenamePlanner();
        var replace = new ReplaceTextRule { Find = "^[abc]$", Replacement = "Foto", UseRegex = true };

        var errors = planner.Plan(_lib.Root, planner.Scan(_lib.Root, new BatchRenameOptions()), new BatchRenameOptions { Rules = [replace] });
        Assert.Equal(3, errors.Count(e => e.Status == PlanStatus.Error));

        var options = new BatchRenameOptions { Rules = [replace], NumberDuplicates = true };
        var plan = planner.Plan(_lib.Root, planner.Scan(_lib.Root, options), options);
        Assert.Equal(["Foto (2).txt", "Foto (3).txt", "Foto (4).txt"], plan.Where(e => e.Status == PlanStatus.Ready).Select(e => e.TargetName));
    }

    [Fact]
    public void Copy_keeps_the_originals_names_taken()
    {
        _lib.File("a.txt");
        _lib.File("b.txt");
        var planner = new BatchRenamePlanner();
        // Copia "a" su "b" nella stessa cartella: con la copia "b.txt" resta, quindi il nome è occupato.
        var options = new BatchRenameOptions { Rules = [new NewNameRule { Pattern = "b" }], Action = BatchAction.Copy, Filter = "a.*" };
        var plan = planner.Plan(_lib.Root, planner.Scan(_lib.Root, options), options);
        Assert.Equal(PlanStatus.Error, Assert.Single(plan).Status);

        var numbered = planner.Plan(_lib.Root, planner.Scan(_lib.Root, options), options with { NumberDuplicates = true });
        Assert.Equal("b (2).txt", Assert.Single(numbered).TargetName);
    }

    [Fact]
    public void Bad_destinations_and_subfolders_are_row_errors()
    {
        _lib.File("a.txt");
        var planner = new BatchRenamePlanner();
        var missing = new BatchRenameOptions { Action = BatchAction.Move, DestinationFolder = Path.Combine(_out.Root, "non-esiste") };
        Assert.Equal(PlanStatus.Error, Assert.Single(planner.Plan(_lib.Root, planner.Scan(_lib.Root, missing), missing)).Status);

        var escape = new BatchRenameOptions { Action = BatchAction.Move, SubfolderPattern = "../fuori" };
        Assert.Equal(PlanStatus.Error, Assert.Single(planner.Plan(_lib.Root, planner.Scan(_lib.Root, escape), escape)).Status);

        // Un dato mancante non lascia livelli vuoti: "{scatto}" su un .txt è vuoto e il file va in "Foto".
        var empty = new BatchRenameOptions { Action = BatchAction.Move, SubfolderPattern = "Foto/{scatto}" };
        var entry = Assert.Single(planner.Plan(_lib.Root, planner.Scan(_lib.Root, empty), empty));
        Assert.Equal(Path.Combine(_lib.Root, "Foto", "a.txt"), entry.TargetPath);

        // "Rinomina" ignora le sottocartelle.
        var rename = new BatchRenameOptions { SubfolderPattern = "Foto", Rules = [new NewNameRule { Pattern = "b" }] };
        Assert.Equal(Path.Combine(_lib.Root, "b.txt"), Assert.Single(planner.Plan(_lib.Root, planner.Scan(_lib.Root, rename), rename)).TargetPath);
    }

    [Fact]
    public async Task Folders_are_renamed_in_place_and_undo_brings_them_back()
    {
        _lib.File(Path.Combine("vacanze.2023", "foto.jpg"));
        _lib.File(Path.Combine("lavoro", "doc.txt"));
        _lib.File("file.txt");
        using var sp = Services();
        // Anche se si sceglie "Copia", le cartelle si rinominano soltanto.
        var options = new BatchRenameOptions { Items = BatchItems.Folders, Action = BatchAction.Copy, Rules = [new ChangeCaseRule { Mode = CaseMode.TitleCase }] };

        var planner = sp.GetRequiredService<BatchRenamePlanner>();
        var scanned = planner.Scan(_lib.Root, options);
        Assert.Equal(["lavoro", "vacanze.2023"], scanned.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.All(scanned, f => Assert.Equal(string.Empty, f.Extension));

        var (result, journal) = await Run(sp, options);
        Assert.All(result, e => Assert.Equal(PlanStatus.Done, e.Status));
        Assert.Equal(["Lavoro", "Lavoro/doc.txt", "Vacanze.2023", "Vacanze.2023/foto.jpg", "file.txt"], Tree(_lib.Root));

        Assert.Empty(await sp.GetRequiredService<RenameExecutor>().UndoAsync(journal!, CancellationToken.None));
        Assert.Equal(["file.txt", "lavoro", "lavoro/doc.txt", "vacanze.2023", "vacanze.2023/foto.jpg"], Tree(_lib.Root));
    }

    public void Dispose()
    {
        _lib.Dispose();
        _out.Dispose();
    }
}
