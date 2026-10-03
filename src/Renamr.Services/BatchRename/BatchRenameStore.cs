using System.Text.Json;
using System.Text.Json.Serialization;
using Renamr.Core.BatchRename;
using Renamr.Services.Providers;

namespace Renamr.Services.BatchRename;

/// <summary>Le scelte della modalità "Rinomina file" salvate tra un avvio e l'altro.</summary>
public sealed record BatchRenameState
{
    /// <summary>L'ultima modalità usata: all'avvio l'app riparte da lì.</summary>
    public bool BatchModeActive { get; init; }

    public BatchRenameOptions Options { get; init; } = BatchRenameOptions.Default;
}

/// <summary>
/// <c>batch-rename.json</c> accanto a settings.json. File separato perché le regole cambiano a ogni tasto:
/// nessun rischio di toccare le chiavi API cifrate. Un file illeggibile fa ripartire dai default.
/// </summary>
public sealed class BatchRenameStore(IAppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    private string FilePath => Path.Combine(paths.DataDirectory, "batch-rename.json");

    public BatchRenameState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new BatchRenameState();
            }
            var state = JsonSerializer.Deserialize<BatchRenameState>(File.ReadAllText(FilePath), Json);
            return state is null ? new BatchRenameState() : state with { Options = state.Options ?? BatchRenameOptions.Default };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new BatchRenameState();
        }
    }

    /// <summary>Scrittura atomica (temp + replace). Un errore qui non deve mai disturbare l'utente: si perde solo il ricordo.</summary>
    public void Save(BatchRenameState state)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }
}
