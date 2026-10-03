using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Renamr.Services.Resilience;

/// <summary>
/// Pipeline Polly v8 per i client che NON passano da IHttpClientFactory (es. TMDbLib, MusicBrainz).
/// I client REST "nostri" usano invece AddStandardResilienceHandler (vedi ServiceCollectionExtensions).
/// </summary>
public static class ResiliencePipelines
{
    public const string Tmdb = "tmdb";
    public const string MusicBrainz = "musicbrainz";

    /// <summary>Retry esponenziale con jitter + circuit breaker + timeout per tentativo.</summary>
    public static ResiliencePipeline CreateDefault(Func<Exception, bool> isTransient, int maxRetries = 3) =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = maxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(500),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(isTransient),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Se il servizio è giù, smettiamo di martellarlo: il resolver passa subito al provider successivo.
                FailureRatio = 0.5,
                MinimumThroughput = 6,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(isTransient),
            })
            .AddTimeout(TimeSpan.FromSeconds(15))
            .Build();

    public static bool IsTransientHttp(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true, // rete
        HttpRequestException { StatusCode: { } code } => code is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)code >= 500,
        TimeoutRejectedException or TaskCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };
}
