using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Models;
using Renamr.Core.Parsing;
using Renamr.Core.Templating;
using Renamr.Services.IO;
using Renamr.Services.Metadata;
using Renamr.Services.Pipeline;
using Renamr.Services.Providers;

namespace Renamr.Tests;

/// <summary>Flusso completo Analisi -> Esecuzione su una libreria temporanea, con un resolver finto (niente rete).</summary>
public class PipelineTests : IDisposable
{
    private readonly TempLibrary _lib = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly RenameJournal _journal;
    private readonly FakeResolver _resolver = new();

    public PipelineTests() => _journal = new RenameJournal(new DefaultAppPaths(Path.Combine(_lib.Root, ".appdata")));

    private RenamePlanner Planner() => new(new MediaScanner(_settings), new SceneCleaner(), _resolver, new NameTemplateEngine(), new TagLibMetadataReader(), _settings);

    private RenameExecutor Executor()
    {
        var io = new SafeFileOperations { TransientRetries = 0 };
        return new RenameExecutor(new MediaFileProcessor(io, new TagLibMetadataWriter(), _journal, _settings), _journal, io);
    }

    [Fact]
    public async Task Movie_is_renamed_tagged_and_dated()
    {
        var src = _lib.Fixture("sample.mp4", "Downloads/The.Matrix.1999.1080p.BluRay.x264-FGT.mp4");
        _lib.File("Downloads/The.Matrix.1999.1080p.BluRay.x264-FGT.it.srt", "sub");

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        var entry = Assert.Single(plan);
        Assert.Equal(PlanStatus.Ready, entry.Status);
        Assert.Equal("The Matrix (1999) [1080p].mp4", entry.TargetName);

        var results = await Executor().ExecuteAsync(_lib.Root, plan, new RenameRunOptions(), null, CancellationToken.None);

        var done = Assert.Single(results);
        Assert.Equal(PlanStatus.Done, done.Status);
        Assert.Null(done.Error);
        var target = Path.Combine(_lib.Root, "Downloads", "The Matrix (1999) [1080p].mp4");
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(_lib.Root, "Downloads", "The Matrix (1999) [1080p].it.srt")));
        Assert.Equal(new DateTime(1999, 3, 31, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(target));

        using var tagged = TagLib.File.Create(target);
        Assert.Equal(1999u, tagged.Tag.Year);
    }

    [Fact]
    public async Task Dry_run_touches_nothing()
    {
        var src = _lib.Fixture("sample.mp4", "The.Matrix.1999.mp4");
        var before = File.ReadAllBytes(src);
        var stamp = File.GetLastWriteTimeUtc(src);

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        var results = await Executor().ExecuteAsync(_lib.Root, plan, new RenameRunOptions { DryRun = true }, null, CancellationToken.None);

        Assert.Equal(PlanStatus.Simulated, Assert.Single(results).Status);
        Assert.True(File.Exists(src));
        Assert.Equal(before, File.ReadAllBytes(src));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(src));
    }

    [Fact]
    public async Task One_locked_file_does_not_stop_the_queue()
    {
        var locked = _lib.Fixture("sample.mp4", "a/The.Matrix.1999.mp4");
        _lib.Fixture("sample.mkv", "b/Inception.2010.mkv");

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        IReadOnlyList<RenamePlanEntry> results;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            results = await Executor().ExecuteAsync(_lib.Root, plan, new RenameRunOptions(), null, CancellationToken.None);
        }

        Assert.Equal(RenamrErrorCode.FileInUse, results.Single(r => r.SourceName.StartsWith("The.Matrix")).Error!.Code);
        Assert.Equal(PlanStatus.Done, results.Single(r => r.SourceName.StartsWith("Inception")).Status);
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task Duplicate_targets_are_flagged_in_the_plan()
    {
        _lib.Fixture("sample.mp4", "x/The.Matrix.1999.mp4");
        _lib.Fixture("sample.mp4", "x/The Matrix 1999.mp4");

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);

        Assert.Single(plan, p => p.Status == PlanStatus.Ready);
        Assert.Equal(RenamrErrorCode.TargetAlreadyExists, Assert.Single(plan, p => p.Status == PlanStatus.Error).Error!.Code);
    }

    [Fact]
    public async Task No_match_is_reported_per_file()
    {
        _lib.Fixture("sample.mkv", "Unknown.Thing.2001.mkv");
        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        Assert.Equal(RenamrErrorCode.NoDatabaseMatch, Assert.Single(plan).Error!.Code);
    }

    [Fact]
    public async Task Readonly_file_is_renamed_and_stays_readonly()
    {
        var src = _lib.Fixture("sample.mp4", "The.Matrix.1999.mp4");
        File.SetAttributes(src, FileAttributes.ReadOnly);

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        var result = Assert.Single(await Executor().ExecuteAsync(_lib.Root, plan, new RenameRunOptions(), null, CancellationToken.None));

        Assert.Equal(PlanStatus.Done, result.Status);
        Assert.True(File.GetAttributes(result.TargetPath!).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Folder_template_organizes_and_undo_restores()
    {
        _settings.Current.Templates.Movie = "Film/{Title} ({Year})/{Title} ({Year})";
        var src = _lib.Fixture("sample.mkv", "dl/The.Matrix.1999.mkv");

        var plan = await Planner().PlanAsync(_lib.Root, null, CancellationToken.None);
        await Executor().ExecuteAsync(_lib.Root, plan, new RenameRunOptions(), null, CancellationToken.None);

        var organized = Path.Combine(_lib.Root, "Film", "The Matrix (1999)", "The Matrix (1999).mkv");
        Assert.True(File.Exists(organized));

        var errors = await Executor().UndoAsync(_journal.CurrentFile!, CancellationToken.None);
        Assert.Empty(errors);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(organized));
    }

    public void Dispose()
    {
        _journal.Dispose();
        _lib.Dispose();
    }

    private sealed class FakeResolver : IMetadataResolver
    {
        public Task<MatchResult> ResolveAsync(MediaQuery query, CancellationToken cancellationToken)
        {
            MediaMetadata? md = query.Title switch
            {
                "The Matrix" => new() { Kind = MediaKind.Movie, Provider = "Fake", ProviderId = "603", Title = "The Matrix", ReleaseDate = new DateOnly(1999, 3, 31) },
                "Inception" => new() { Kind = MediaKind.Movie, Provider = "Fake", ProviderId = "27205", Title = "Inception", ReleaseDate = new DateOnly(2010, 7, 16) },
                _ => null,
            };
            return Task.FromResult(md is null
                ? MatchResult.NoMatch(["Fake: nessun risultato"])
                : new MatchResult { Outcome = MatchOutcome.Matched, Best = new MatchCandidate(md, 0.97) });
        }
    }
}
