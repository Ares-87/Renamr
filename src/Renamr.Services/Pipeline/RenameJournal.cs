using System.Text.Json;
using Renamr.Services.Providers;

namespace Renamr.Services.Pipeline;

/// <summary>
/// Diario append-only delle ridenominazioni effettive (JSON Lines). Ogni riga è scritta e forzata su disco
/// <i>subito dopo</i> lo spostamento: anche dopo un crash si sa esattamente cosa è stato fatto e si può annullare.
/// </summary>
public sealed class RenameJournal(IAppPaths paths) : IDisposable
{
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    public string? CurrentFile { get; private set; }

    public void BeginSession(string rootFolder)
    {
        lock (_gate)
        {
            _writer?.Dispose();
            Directory.CreateDirectory(paths.JournalDirectory);
            // Due sessioni nello stesso secondo (rinomine ravvicinate) non devono scontrarsi sullo stesso file.
            var stamp = $"{DateTime.Now:yyyyMMdd-HHmmss}";
            CurrentFile = Path.Combine(paths.JournalDirectory, $"{stamp}.jsonl");
            for (var n = 2; File.Exists(CurrentFile); n++)
            {
                CurrentFile = Path.Combine(paths.JournalDirectory, $"{stamp}-{n}.jsonl");
            }
            _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            Write(new JournalEntry("session", rootFolder, null, DateTimeOffset.Now));
        }
    }

    /// <param name="root">Cartella-recinto del file: "Annulla" ricontrolla lo spostamento all'indietro dentro questa.</param>
    public void RecordMove(string source, string target, string? root = null)
    {
        lock (_gate)
        {
            Write(new JournalEntry("move", source, target, DateTimeOffset.Now, root));
        }
    }

    /// <summary>Sposta in un'altra cartella: la destinazione ha il suo recinto, <paramref name="targetRoot"/>.</summary>
    public void RecordMove(string source, string target, string? root, string? targetRoot)
    {
        lock (_gate)
        {
            Write(new JournalEntry("move", source, target, DateTimeOffset.Now, root, targetRoot));
        }
    }

    /// <summary>Una copia creata da noi: "Annulla" la toglie, l'originale non è mai stato toccato.</summary>
    public void RecordCopy(string source, string target, string? root, string? targetRoot)
    {
        lock (_gate)
        {
            Write(new JournalEntry("copy", source, target, DateTimeOffset.Now, root, targetRoot));
        }
    }

    /// <summary>Una cartella creata da noi per sposta o copia: "Annulla" la toglie se è rimasta vuota.</summary>
    public void RecordFolder(string folder, string? root)
    {
        lock (_gate)
        {
            Write(new JournalEntry("folder", folder, null, DateTimeOffset.Now, root));
        }
    }

    public static IReadOnlyList<JournalEntry> Read(string journalFile) =>
        File.ReadLines(journalFile)
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<JournalEntry>(l)!)
            .ToList();

    private void Write(JournalEntry entry)
    {
        if (_writer is null)
        {
            return;
        }
        _writer.WriteLine(JsonSerializer.Serialize(entry));
        ((FileStream)_writer.BaseStream).Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public sealed record JournalEntry(string Kind, string Source, string? Target, DateTimeOffset At, string? Root = null, string? TargetRoot = null);
