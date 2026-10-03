using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;
using Renamr.Core.Matching;
using Renamr.Core.Models;

namespace Renamr.Services.Providers.Music;

/// <summary>
/// Riconoscimento acustico: impronta Chromaprint calcolata con <c>fpcalc.exe</c> (distribuito con l'app in Tools\),
/// poi lookup su AcoustID che restituisce direttamente gli ID MusicBrainz di registrazione e release.
/// <para>
/// Scelta progettuale: AcoustID.NET calcola l'impronta in-process ma richiede un decoder audio (NAudio/Bass) e una
/// manutenzione ferma da anni; <c>fpcalc</c> è il binario ufficiale Chromaprint, decodifica tutti i formati via FFmpeg
/// e gira in un processo separato (un file corrotto non può far cadere l'app).
/// </para>
/// </summary>
public sealed class AcoustIdProvider(HttpClient http, ISettingsStore settings) : IMetadataProvider
{
    public const string HttpClientName = "acoustid";

    public string Name => "AcoustID";
    public int Priority => 10;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Current.Keys.AcoustIdClientKey) && FpcalcPath is not null;
    public bool Supports(MediaKind kind) => kind == MediaKind.Music;

    public static string? FpcalcPath { get; } = FindFpcalc();

    public async Task<IReadOnlyList<MatchCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        if (query.FilePath is null)
        {
            return [];
        }

        try
        {
            var fp = await ComputeFingerprintAsync(query.FilePath, cancellationToken).ConfigureAwait(false);
            if (fp is null)
            {
                return [];
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client"] = settings.Current.Keys.AcoustIdClientKey!,
                ["duration"] = ((int)Math.Round(fp.Duration)).ToString(CultureInfo.InvariantCulture),
                ["fingerprint"] = fp.Fingerprint,
                ["meta"] = "recordings releases releasegroups compress",
                ["format"] = "json",
            });
            using var response = await http.PostAsync("lookup", form, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var lookup = await response.Content.ReadFromJsonAsync<Lookup>(cancellationToken).ConfigureAwait(false);

            if (lookup?.Status != "ok")
            {
                throw new ProviderException(Name, RenamrErrorCode.ProviderAuthFailed, lookup?.Error?.Message ?? "Risposta AcoustID non valida");
            }

            var candidates = new List<MatchCandidate>();
            foreach (var r in lookup.Results ?? [])
            {
                foreach (var rec in r.Recordings ?? [])
                {
                    var artist = rec.Artists is { Count: > 0 } a ? string.Join(", ", a.Select(x => x.Name)) : null;
                    // La release più antica è la data "reale" dell'opera (non la ristampa rimasterizzata).
                    var release = (rec.Releases ?? [])
                        .Where(x => x.Date?.Year is not null)
                        .OrderBy(x => x.Date!.ToDateOnly())
                        .FirstOrDefault();

                    // Se l'utente ha già l'album nei tag, preferiamo quella release.
                    if (query.Album is not null)
                    {
                        release = (rec.Releases ?? []).FirstOrDefault(x => TitleSimilarity.Score(x.Title, query.Album) > 0.85) ?? release;
                    }

                    var track = release?.Mediums?.SelectMany(m => m.Tracks ?? []).FirstOrDefault();
                    candidates.Add(new MatchCandidate(new MediaMetadata
                    {
                        Kind = MediaKind.Music,
                        Provider = Name,
                        ProviderId = rec.Id ?? r.Id ?? string.Empty,
                        Title = rec.Title ?? query.Title,
                        Artist = artist,
                        Album = release?.Title,
                        TrackNumber = track?.Position,
                        DiscNumber = release?.Mediums?.FirstOrDefault()?.Position,
                        ReleaseDate = release?.Date?.ToDateOnly(),
                        YearOnly = release?.Date?.Year,
                    }, Math.Round(r.Score, 3)));
                }
            }
            return candidates.OrderByDescending(c => c.Confidence).Take(5).ToList();
        }
        catch (Exception ex) when (ProviderHelpers.ShouldWrap(ex, cancellationToken))
        {
            throw ProviderHelpers.Wrap(Name, ex);
        }
    }

    private static async Task<FpcalcResult?> ComputeFingerprintAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FpcalcPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add(path); // ArgumentList: niente problemi di quoting con nomi strani

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("fpcalc non avviato");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? JsonSerializer.Deserialize<FpcalcResult>(output) : null;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static string? FindFpcalc()
    {
        var exe = OperatingSystem.IsWindows() ? "fpcalc.exe" : "fpcalc";
        var local = Path.Combine(AppContext.BaseDirectory, "Tools", exe);
        if (File.Exists(local))
        {
            return local;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
    }

    private sealed record FpcalcResult([property: JsonPropertyName("duration")] double Duration, [property: JsonPropertyName("fingerprint")] string Fingerprint);

    private sealed record Lookup(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("error")] LookupError? Error,
        [property: JsonPropertyName("results")] List<LookupResult>? Results);

    private sealed record LookupError([property: JsonPropertyName("message")] string? Message);

    private sealed record LookupResult(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("recordings")] List<Recording>? Recordings);

    private sealed record Recording(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("artists")] List<Artist>? Artists,
        [property: JsonPropertyName("releases")] List<Release>? Releases);

    private sealed record Artist([property: JsonPropertyName("name")] string? Name);

    private sealed record Release(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("date")] PartialDate? Date,
        [property: JsonPropertyName("mediums")] List<Medium>? Mediums);

    private sealed record Medium([property: JsonPropertyName("position")] int? Position, [property: JsonPropertyName("tracks")] List<Track>? Tracks);
    private sealed record Track([property: JsonPropertyName("position")] int? Position);

    private sealed record PartialDate(
        [property: JsonPropertyName("year")] int? Year,
        [property: JsonPropertyName("month")] int? Month,
        [property: JsonPropertyName("day")] int? Day)
    {
        public DateOnly? ToDateOnly() => Year is { } y ? new DateOnly(y, Month ?? 1, Day ?? 1) : null;
    }
}
