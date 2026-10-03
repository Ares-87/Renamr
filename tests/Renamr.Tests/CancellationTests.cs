using System.Diagnostics;
using System.Net;
using Renamr.Services.Resilience;

namespace Renamr.Tests;

/// <summary>Annulla durante l'analisi: risposta immediata, senza chiudere a forza le richieste di rete già partite.</summary>
public class CancellationTests
{
    [Fact]
    public async Task Cancel_stops_waiting_at_once_but_leaves_the_call_running()
    {
        var callToken = CancellationToken.None;
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var task = AbandonOnCancel.RunAsync(t => { callToken = t; return finish.Task; }, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(callToken.IsCancellationRequested); // il socket non viene chiuso a metà
        finish.SetResult("tardi"); // la risposta arrivata dopo viene semplicemente ignorata
    }

    [Fact]
    public async Task Already_cancelled_does_not_start_the_call()
    {
        var started = false;
        var task = AbandonOnCancel.RunAsync(_ => { started = true; return Task.FromResult(1); }, new CancellationToken(canceled: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(started);
    }

    [Fact]
    public async Task Abandoned_call_still_ends_at_the_safety_timeout()
    {
        var callToken = CancellationToken.None;
        using var cts = new CancellationTokenSource();
        var task = AbandonOnCancel.RunAsync(t => { callToken = t; return Task.Delay(Timeout.Infinite, t).ContinueWith(_ => 0, TaskScheduler.Default); },
            cts.Token, TimeSpan.FromMilliseconds(100));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        var sw = Stopwatch.StartNew();
        while (!callToken.IsCancellationRequested && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }
        Assert.True(callToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Http_handler_returns_on_cancel_and_disposes_the_late_response()
    {
        var inner = new StallingHandler();
        using var client = new HttpClient(new AbandonOnCancelHandler(TimeSpan.FromMinutes(1)) { InnerHandler = inner });
        using var cts = new CancellationTokenSource();

        var request = client.GetAsync(new Uri("http://example.invalid/"), cts.Token);
        await inner.Started.Task;
        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
        Assert.False(inner.Token.IsCancellationRequested);

        var late = new TrackingContent();
        inner.Release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = late });
        var wait = Stopwatch.StartNew();
        while (!late.Disposed && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20);
        }
        Assert.True(late.Disposed);
    }

    [Fact]
    public async Task Http_handler_passes_responses_through_with_the_body_already_read()
    {
        var inner = new StallingHandler();
        using var client = new HttpClient(new AbandonOnCancelHandler(TimeSpan.FromMinutes(1)) { InnerHandler = inner });
        inner.Release.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });

        var body = await client.GetStringAsync(new Uri("http://example.invalid/"));
        Assert.Equal("{\"ok\":true}", body);
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class TrackingContent : StringContent
    {
        public TrackingContent() : base("tardi") { }
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
