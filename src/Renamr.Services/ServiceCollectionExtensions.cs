using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Renamr.Core.Abstractions;
using Renamr.Core.Parsing;
using Renamr.Core.Templating;
using Renamr.Services.BatchRename;
using Renamr.Services.IO;
using Renamr.Services.Matching;
using Renamr.Services.Metadata;
using Renamr.Services.Pipeline;
using Renamr.Services.Providers;
using Renamr.Services.Providers.Movies;
using Renamr.Services.Providers.Music;
using Renamr.Services.Providers.Tmdb;
using Renamr.Services.Providers.Tv;
using Renamr.Services.Resilience;

namespace Renamr.Services;

public static class ServiceCollectionExtensions
{
    private static readonly ProductInfoHeaderValue UserAgent = new("Renamr", "1.0");

    /// <summary>
    /// Registra tutto il backend. L'host (app WinUI o test) deve registrare un <see cref="ISettingsStore"/>.
    /// I provider sono singleton: mantengono cache (titoli AniDB, token TheTVDB) e throttle globali.
    /// </summary>
    public static IServiceCollection AddRenamrServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths>(_ => new DefaultAppPaths());

        // Dominio
        services.AddSingleton<IFileNameParser, SceneCleaner>();
        services.AddSingleton<INameTemplateEngine, NameTemplateEngine>();

        // I/O
        services.AddSingleton<SafeFileOperations>();
        services.AddSingleton<IEmbeddedMetadataWriter, TagLibMetadataWriter>();
        services.AddSingleton<IEmbeddedMetadataReader, TagLibMetadataReader>();

        // HttpClient con pipeline Polly standard: rate limiter, timeout totale, retry esponenziale con jitter,
        // circuit breaker, timeout per tentativo. Rispetta anche l'header Retry-After sui 429.
        AddApiClient(services, OmdbProvider.HttpClientName, "https://www.omdbapi.com/");
        AddApiClient(services, TvMazeProvider.HttpClientName, "https://api.tvmaze.com/");
        AddApiClient(services, TheTvdbProvider.HttpClientName, "https://api4.thetvdb.com/v4/");
        AddApiClient(services, AcoustIdProvider.HttpClientName, "https://api.acoustid.org/v2/");
        AddApiClient(services, MusicBrainzProvider.HttpClientName, "https://musicbrainz.org/ws/2/");
        AddApiClient(services, AniDbProvider.HttpClientName, null, totalTimeout: TimeSpan.FromMinutes(3)); // dump titoli ~10 MB

        // Provider: aggiungerne uno = una riga qui.
        services.AddSingleton<TmdbClientAccessor>();
        services.AddSingleton<IMetadataProvider, TmdbMovieProvider>();
        services.AddSingleton<IMetadataProvider, TmdbTvProvider>();
        services.AddSingleton<IMetadataProvider>(sp => new OmdbProvider(Client(sp, OmdbProvider.HttpClientName), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IMetadataProvider>(sp => new TheTvdbProvider(Client(sp, TheTvdbProvider.HttpClientName), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IMetadataProvider>(sp => new TvMazeProvider(Client(sp, TvMazeProvider.HttpClientName), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IMetadataProvider>(sp => ActivatorUtilities.CreateInstance<AniDbProvider>(sp, Client(sp, AniDbProvider.HttpClientName)));
        services.AddSingleton<IMetadataProvider>(sp => new AcoustIdProvider(Client(sp, AcoustIdProvider.HttpClientName), sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IMetadataProvider>(sp => new MusicBrainzProvider(Client(sp, MusicBrainzProvider.HttpClientName)));
        services.AddSingleton<ProviderHealth>();
        services.AddSingleton<IMetadataResolver, CascadingMetadataResolver>();

        // Pipeline
        services.AddSingleton<MediaScanner>();
        services.AddSingleton<RenameJournal>();
        services.AddSingleton<MediaFileProcessor>();
        services.AddTransient<RenamePlanner>();
        services.AddTransient<RenameExecutor>();

        // Modalità "Rinomina file": qualunque file, solo il nome.
        services.AddSingleton<BatchRenamePlanner>();
        services.AddSingleton<BatchRenameStore>();
        services.AddTransient<BatchRenameExecutor>();
        return services;
    }

    private static HttpClient Client(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    private static void AddApiClient(IServiceCollection services, string name, string? baseAddress, TimeSpan? totalTimeout = null)
    {
        services.AddHttpClient(name, client =>
            {
                if (baseAddress is not null)
                {
                    client.BaseAddress = new Uri(baseAddress);
                }
                client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/Ares-87/Renamr)"));
            })
            // Prima della pipeline standard = handler più esterno: Annulla non chiude i socket a metà (vedi AbandonOnCancel).
            .AddHttpMessageHandler(() => new AbandonOnCancelHandler((totalTimeout ?? TimeSpan.FromSeconds(45)) + TimeSpan.FromSeconds(15)))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.Retry.UseJitter = true;
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(totalTimeout is null ? 10 : 90);
                options.TotalRequestTimeout.Timeout = totalTimeout ?? TimeSpan.FromSeconds(45);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(Math.Max(30, options.AttemptTimeout.Timeout.TotalSeconds * 2));
            });
    }
}
