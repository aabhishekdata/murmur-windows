using System.Net;
using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Testing;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>Loading Parakeet only when needed, and letting it go again.</summary>
public sealed class LazyTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[1600];

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        condition().ShouldBeTrue(because);
    }

    [Fact]
    public async Task Loads_once_on_first_use_and_not_before()
    {
        var built = new List<ScriptedTranscriber>();
        await using var lazy = new LazyTranscriber(
            () =>
            {
                var inner = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("hi");
                built.Add(inner);
                return inner;
            },
            TranscriberKind.Parakeet,
            time: new ManualTimeProvider());

        lazy.IsLoaded.ShouldBeFalse("nothing is loaded until something asks");
        built.ShouldBeEmpty();

        (await lazy.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("hi");
        (await lazy.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("hi");

        built.Count.ShouldBe(1);
        built[0].LoadCalls.ShouldBe(1);
        built[0].Calls.ShouldBe(2);
        lazy.IsReady.ShouldBeTrue();
        lazy.Kind.ShouldBe(TranscriberKind.Parakeet);
    }

    [Fact]
    public async Task Unloads_after_sitting_idle_and_reloads_on_next_use()
    {
        var time = new ManualTimeProvider();
        var built = new List<ScriptedTranscriber>();
        await using var lazy = new LazyTranscriber(
            () =>
            {
                var inner = new ScriptedTranscriber().Returns("x");
                built.Add(inner);
                return inner;
            },
            TranscriberKind.Parakeet,
            unloadAfter: () => TimeSpan.FromMinutes(10),
            time: time);

        var unloaded = 0;
        lazy.Unloaded += (_, _) => unloaded++;

        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(9));
        lazy.IsLoaded.ShouldBeTrue("nine minutes is not ten");

        time.Advance(TimeSpan.FromMinutes(1));
        lazy.IsLoaded.ShouldBeFalse();
        built[0].DisposeCalls.ShouldBe(1);
        unloaded.ShouldBe(1);

        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);
        built.Count.ShouldBe(2, "a fresh instance is built on the next load");
    }

    [Fact]
    public async Task Each_use_restarts_the_idle_clock()
    {
        var time = new ManualTimeProvider();
        var inner = new ScriptedTranscriber().Returns("x");
        await using var lazy = new LazyTranscriber(
            () => inner, TranscriberKind.Parakeet, unloadAfter: () => TimeSpan.FromMinutes(10), time: time);

        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(8));
        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(8));

        lazy.IsLoaded.ShouldBeTrue("eight minutes since the last use");
    }

    [Fact]
    public async Task Keep_warm_holds_it_loaded_and_letting_go_unloads_it_later()
    {
        var time = new ManualTimeProvider();
        var warm = true;
        var inner = new ScriptedTranscriber().Returns("x");
        await using var lazy = new LazyTranscriber(
            () => inner, TranscriberKind.Parakeet,
            keepWarm: () => warm,
            unloadAfter: () => TimeSpan.FromMinutes(10),
            time: time);

        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);
        time.Advance(TimeSpan.FromHours(3));
        lazy.IsLoaded.ShouldBeTrue("keep-warm means never unload");

        // Switching away from Parakeet mode must eventually free it, even with no further use.
        warm = false;
        time.Advance(TimeSpan.FromMinutes(10));
        lazy.IsLoaded.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unload_that_comes_due_mid_decode_waits_for_the_decode()
    {
        var time = new ManualTimeProvider();
        var inner = new ScriptedTranscriber().Returns("warm").HangsUntilReleased("slow");
        await using var lazy = new LazyTranscriber(
            () => inner, TranscriberKind.Parakeet, unloadAfter: () => TimeSpan.FromMinutes(10), time: time);

        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);
        (await inner.WaitForCallAsync()).ShouldBeTrue();

        var decoding = lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask();
        (await inner.WaitForCallAsync()).ShouldBeTrue();

        // The unload comes due while the decode is still running.
        time.Advance(TimeSpan.FromMinutes(11));
        inner.DisposeCalls.ShouldBe(0, "the model must not be freed under a running decode");

        inner.Release();
        (await decoding).ShouldBe("slow");
        inner.DisposedWhileInFlight.ShouldBeFalse();

        // It was just used, so the queued unload stands down; the next idle period frees it.
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
        lazy.IsLoaded.ShouldBeTrue("used a moment ago");

        time.Advance(TimeSpan.FromMinutes(10));
        await WaitForAsync(() => !lazy.IsLoaded, "ten idle minutes after the decode");
        inner.DisposedWhileInFlight.ShouldBeFalse();
    }

    [Fact]
    public async Task No_model_is_a_NoModel_failure_and_is_looked_for_again_next_time()
    {
        string? directory = null;
        var built = 0;
        await using var lazy = new LazyTranscriber(
            () =>
            {
                if (directory is null) return null;
                built++;
                return new ScriptedTranscriber().Returns("found it");
            },
            TranscriberKind.Parakeet,
            time: new ManualTimeProvider());

        var failure = await Should.ThrowAsync<TranscriptionException>(
            () => lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask());
        failure.Failure.ShouldBe(TranscriptionFailure.NoModel);
        failure.IsTransient.ShouldBeFalse();

        // The download finishes while the app is running.
        directory = "models";
        (await lazy.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("found it");
        built.ShouldBe(1);
    }

    [Fact]
    public async Task A_model_that_will_not_load_is_disposed_and_not_kept()
    {
        var inner = new ScriptedTranscriber { LoadResult = false };
        await using var lazy = new LazyTranscriber(() => inner, TranscriberKind.Parakeet, time: new ManualTimeProvider());

        (await lazy.LoadAsync(CancellationToken.None)).ShouldBeFalse();
        lazy.IsLoaded.ShouldBeFalse();
        inner.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Warm_up_loads_ahead_of_use_and_still_unloads_if_never_used()
    {
        var time = new ManualTimeProvider();
        var inner = new ScriptedTranscriber();
        await using var lazy = new LazyTranscriber(
            () => inner, TranscriberKind.Parakeet, unloadAfter: () => TimeSpan.FromMinutes(10), time: time);

        (await lazy.WarmUpAsync(CancellationToken.None)).ShouldBeTrue();
        lazy.IsReady.ShouldBeTrue();
        inner.Calls.ShouldBe(0);

        time.Advance(TimeSpan.FromMinutes(10));
        lazy.IsLoaded.ShouldBeFalse("a warm-up the user never followed with speech must not pin the model");
    }

    [Fact]
    public async Task Dispose_frees_the_loaded_model()
    {
        var inner = new ScriptedTranscriber().Returns("x");
        var lazy = new LazyTranscriber(() => inner, TranscriberKind.Parakeet, time: new ManualTimeProvider());
        await lazy.TranscribeAsync(Audio, [], CancellationToken.None);

        await lazy.DisposeAsync();

        inner.DisposeCalls.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(
            () => lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Quitting_mid_decode_waits_for_it_then_frees_the_model()
    {
        // The system clock on purpose, so the real timer is the one being disposed.
        var inner = new ScriptedTranscriber().HangsUntilReleased("late");
        var lazy = new LazyTranscriber(() => inner, TranscriberKind.Parakeet);

        var decoding = lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask();
        (await inner.WaitForCallAsync()).ShouldBeTrue();

        var disposing = lazy.DisposeAsync().AsTask();
        await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
        disposing.IsCompleted.ShouldBeFalse("the decode still holds the model");

        inner.Release();
        (await decoding).ShouldBe("late");
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);

        inner.DisposeCalls.ShouldBe(1);
        inner.DisposedWhileInFlight.ShouldBeFalse();
    }

    [Fact]
    public async Task Quitting_under_a_decode_that_never_ends_gives_up_waiting_and_leaves_the_model()
    {
        var inner = new ScriptedTranscriber().HangsUntilReleased("never");
        var lazy = new LazyTranscriber(
            () => inner, TranscriberKind.Parakeet, disposeWait: TimeSpan.FromMilliseconds(50));

        var decoding = lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask();
        (await inner.WaitForCallAsync()).ShouldBeTrue();

        await lazy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        inner.DisposeCalls.ShouldBe(0, "freeing the recogniser under a running decode is a use-after-free");

        // If it does end after all, it ends cleanly, and still nothing frees the model under it.
        inner.Release();
        (await decoding).ShouldBe("never");
        inner.DisposedWhileInFlight.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(
            () => lazy.TranscribeAsync(Audio, [], CancellationToken.None).AsTask());
    }
}

/// <summary>Which cloud failures hand the dictation to Parakeet, and which must not.</summary>
public sealed class FallbackTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[1600];

    private static (FallbackTranscriber Fallback, ScriptedTranscriber Cloud, ScriptedTranscriber Local) Build(
        ManualTimeProvider? time = null, TimeSpan? primaryTimeout = null)
    {
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure);
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");
        var fallback = new FallbackTranscriber(
            cloud, local, time ?? new ManualTimeProvider(), () => primaryTimeout);
        return (fallback, cloud, local);
    }

    public static TheoryData<string, Exception> Transient => new()
    {
        { "network", new TranscriptionException(TranscriptionFailure.Network, "offline") },
        { "timeout", new TranscriptionException(TranscriptionFailure.Timeout, "408", 408) },
        { "rate limited", new TranscriptionException(TranscriptionFailure.RateLimited, "429", 429) },
        { "server error", new TranscriptionException(TranscriptionFailure.ServerError, "503", 503) },
        { "http, no status", new HttpRequestException("connection refused") },
        { "http 502", new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway) },
        { "http 429", new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests) },
        { "timeout exception", new TimeoutException() },
        { "HttpClient timeout", new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") },
    };

    public static TheoryData<string, Exception> Permanent => new()
    {
        { "unauthorized", new TranscriptionException(TranscriptionFailure.Unauthorized, "401", 401) },
        { "forbidden", new TranscriptionException(TranscriptionFailure.Forbidden, "403", 403) },
        { "not found", new TranscriptionException(TranscriptionFailure.NotFound, "404", 404) },
        { "bad request", new TranscriptionException(TranscriptionFailure.BadRequest, "400", 400) },
        { "not configured", new TranscriptionException(TranscriptionFailure.NotConfigured, "no key") },
        { "unknown", new TranscriptionException(TranscriptionFailure.Unknown, "?") },
        { "http 401", new HttpRequestException("no", null, HttpStatusCode.Unauthorized) },
        { "http 403", new HttpRequestException("no", null, HttpStatusCode.Forbidden) },
        { "http 404", new HttpRequestException("no", null, HttpStatusCode.NotFound) },
        { "http 400", new HttpRequestException("no", null, HttpStatusCode.BadRequest) },
        { "a bug", new InvalidOperationException("boom") },
    };

    [Theory]
    [MemberData(nameof(Transient))]
    public async Task Transient_failures_fall_back_to_Parakeet(string name, Exception failure)
    {
        var (fallback, cloud, local) = Build();
        cloud.Throws(failure);

        TranscriptionFallback? raised = null;
        fallback.FellBack += (_, e) => raised = e;

        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("heard locally", name);

        local.Calls.ShouldBe(1);
        fallback.LastFellBack.ShouldBeTrue();
        fallback.LastUsedKind.ShouldBe(TranscriberKind.Parakeet);
        raised.ShouldNotBeNull();
        raised.From.ShouldBe(TranscriberKind.Azure);
        raised.To.ShouldBe(TranscriberKind.Parakeet);
        raised.Cause.ShouldBeSameAs(failure);
    }

    [Theory]
    [MemberData(nameof(Permanent))]
    public async Task Setup_errors_and_bugs_surface_instead_of_falling_back(string name, Exception failure)
    {
        var (fallback, cloud, local) = Build();
        cloud.Throws(failure);

        var thrown = await Should.ThrowAsync<Exception>(
            () => fallback.TranscribeAsync(Audio, [], CancellationToken.None).AsTask());

        thrown.ShouldBeSameAs(failure, name);
        local.Calls.ShouldBe(0, "a broken setup must not be hidden behind the offline engine");
        fallback.IsCircuitOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_dictation_is_never_retried_on_anything()
    {
        var (fallback, cloud, local) = Build();
        cloud.Hangs();

        using var cancel = new CancellationTokenSource();
        var call = fallback.TranscribeAsync(Audio, [], cancel.Token).AsTask();
        (await cloud.WaitForCallAsync()).ShouldBeTrue();

        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call);
        local.Calls.ShouldBe(0);
        fallback.IsCircuitOpen.ShouldBeFalse("the user cancelling says nothing about the cloud");
    }

    [Fact]
    public async Task The_cloud_timing_out_hands_over_to_Parakeet()
    {
        var time = new ManualTimeProvider();
        var (fallback, cloud, local) = Build(time, primaryTimeout: TimeSpan.FromSeconds(5));
        cloud.Hangs();

        var call = fallback.TranscribeAsync(Audio, [], CancellationToken.None).AsTask();
        (await cloud.WaitForCallAsync()).ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(5));

        (await call).ShouldBe("heard locally");
        cloud.ObservedCancellation.ShouldBeTrue("the cloud request is abandoned, not left running");
        fallback.LastFellBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Success_reports_the_cloud_and_no_fallback()
    {
        var (fallback, cloud, local) = Build();
        cloud.Returns("heard in the cloud");

        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("heard in the cloud");

        fallback.LastFellBack.ShouldBeFalse();
        fallback.LastUsedKind.ShouldBe(TranscriberKind.Azure);
        local.Calls.ShouldBe(0);
        local.LoadCalls.ShouldBe(0, "Parakeet is not even loaded when it is not needed");
    }

    [Fact]
    public async Task After_a_failure_the_cloud_is_skipped_for_sixty_seconds()
    {
        var time = new ManualTimeProvider();
        var (fallback, cloud, local) = Build(time);
        cloud.Throws(new HttpRequestException("down")).Returns("back");

        var causes = new List<Exception?>();
        fallback.FellBack += (_, e) => causes.Add(e.Cause);

        await fallback.TranscribeAsync(Audio, [], CancellationToken.None);
        fallback.IsCircuitOpen.ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(59));
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("heard locally");
        cloud.Calls.ShouldBe(1, "no second connection timeout to wait out");
        causes.Count.ShouldBe(2);
        causes[1].ShouldBeNull("skipped, not failed");

        time.Advance(TimeSpan.FromSeconds(1));
        fallback.IsCircuitOpen.ShouldBeFalse();
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("back");
        cloud.Calls.ShouldBe(2);
        fallback.LastFellBack.ShouldBeFalse();
    }

    [Fact]
    public async Task The_network_going_down_and_up_opens_and_closes_the_circuit()
    {
        var time = new ManualTimeProvider();
        var (fallback, cloud, local) = Build(time);
        cloud.Throws(new HttpRequestException("down")).Returns("cloud");

        fallback.OnNetworkAvailabilityChanged(isAvailable: false);
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("heard locally");
        cloud.Calls.ShouldBe(0);

        time.Advance(TimeSpan.FromMinutes(5));
        fallback.IsCircuitOpen.ShouldBeTrue("held open for as long as the network is down");

        fallback.OnNetworkAvailabilityChanged(isAvailable: true);
        fallback.IsCircuitOpen.ShouldBeFalse();

        // The first failure after reconnecting trips the breaker; reconnecting resets it at once.
        await fallback.TranscribeAsync(Audio, [], CancellationToken.None);
        fallback.IsCircuitOpen.ShouldBeTrue();
        fallback.OnNetworkAvailabilityChanged(isAvailable: true);
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("cloud");
    }

    [Fact]
    public async Task The_fallback_failing_too_is_reported()
    {
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Throws(new HttpRequestException("down"));
        var noModel = new TranscriptionException(TranscriptionFailure.NoModel, "none");
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Throws(noModel);
        await using var both = new FallbackTranscriber(cloud, local, new ManualTimeProvider());

        (await Should.ThrowAsync<TranscriptionException>(
            () => both.TranscribeAsync(Audio, [], CancellationToken.None).AsTask())).ShouldBeSameAs(noModel);
    }

    [Fact]
    public async Task Without_the_offline_model_the_cloud_is_never_skipped()
    {
        var time = new ManualTimeProvider();
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure);
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");
        var installed = false;
        await using var fallback = new FallbackTranscriber(
            cloud, local, time, fallbackAvailable: () => installed);

        var busy = new TranscriptionException(TranscriptionFailure.RateLimited, "429", 429);
        cloud.Throws(busy).Returns("cloud is back");

        // The cloud's own error, not "no model": that is what the user can act on.
        (await Should.ThrowAsync<TranscriptionException>(
            () => fallback.TranscribeAsync(Audio, [], CancellationToken.None).AsTask())).ShouldBeSameAs(busy);
        local.Calls.ShouldBe(0);
        fallback.IsCircuitOpen.ShouldBeFalse("nothing to fall back to, so nothing to skip the cloud for");

        // The very next dictation asks the cloud, which has recovered.
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("cloud is back");
        fallback.LastFellBack.ShouldBeFalse();

        // The network going down does not route to a fallback that cannot answer either.
        cloud.Returns("still the cloud");
        fallback.OnNetworkAvailabilityChanged(isAvailable: false);
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("still the cloud");
        local.Calls.ShouldBe(0);

        // Once the model is installed, the open circuit is honoured again.
        installed = true;
        (await fallback.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("heard locally");
    }

    [Fact]
    public async Task Without_the_offline_model_a_cloud_timeout_is_reported_as_a_timeout()
    {
        var time = new ManualTimeProvider();
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Hangs();
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("heard locally");
        await using var fallback = new FallbackTranscriber(
            cloud, local, time, () => TimeSpan.FromSeconds(5), fallbackAvailable: () => false);

        var call = fallback.TranscribeAsync(Audio, [], CancellationToken.None).AsTask();
        (await cloud.WaitForCallAsync()).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(5));

        var thrown = await Should.ThrowAsync<TranscriptionException>(() => call);
        thrown.Failure.ShouldBe(TranscriptionFailure.Timeout, "not a cancellation the user never asked for");
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Disposal_leaves_shared_engines_alone_when_asked()
    {
        var cloud = new ScriptedTranscriber();
        var local = new ScriptedTranscriber();

        await new FallbackTranscriber(cloud, local, leaveOpen: true).DisposeAsync();
        cloud.DisposeCalls.ShouldBe(0);

        await new FallbackTranscriber(cloud, local).DisposeAsync();
        cloud.DisposeCalls.ShouldBe(1);
        local.DisposeCalls.ShouldBe(1);
    }
}

/// <summary>Switching engines without a restart.</summary>
public sealed class RoutingTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[1600];

    [Fact]
    public async Task Each_call_goes_to_the_mode_in_force_at_that_moment()
    {
        var mode = EngineMode.Parakeet;
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("local");
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Returns("cloud");
        await using var router = new RoutingTranscriber(() => mode, local, cloud, new ManualTimeProvider());

        router.Kind.ShouldBe(TranscriberKind.Parakeet);
        (await router.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("local");
        router.LastUsedKind.ShouldBe(TranscriberKind.Parakeet);

        mode = EngineMode.Azure;
        router.Kind.ShouldBe(TranscriberKind.Azure);
        (await router.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("cloud");
        router.LastUsedKind.ShouldBe(TranscriberKind.Azure);
        router.LastFellBack.ShouldBeFalse();
    }

    [Fact]
    public async Task Fallback_mode_reports_which_engine_actually_answered()
    {
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("local");
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure)
            .Throws(new TranscriptionException(TranscriptionFailure.ServerError, "500", 500));
        await using var router = new RoutingTranscriber(
            () => EngineMode.AzureWithParakeetFallback, local, cloud, new ManualTimeProvider());

        (await router.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("local");

        router.LastUsedKind.ShouldBe(TranscriberKind.Parakeet);
        router.LastFellBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Cloud_only_mode_never_errs_over_to_Parakeet()
    {
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("local");
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Throws(new HttpRequestException("down"));
        await using var router = new RoutingTranscriber(() => EngineMode.Azure, local, cloud);

        await Should.ThrowAsync<HttpRequestException>(
            () => router.TranscribeAsync(Audio, [], CancellationToken.None).AsTask());
        local.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(EngineMode.Parakeet, true, false)]
    [InlineData(EngineMode.AzureWithParakeetFallback, false, false)]
    [InlineData(EngineMode.AzureWithParakeetFallback, true, true)]
    [InlineData(EngineMode.Azure, false, false)]
    [InlineData(EngineMode.Azure, false, true)]
    public async Task Warm_up_loads_Parakeet_only_when_it_is_going_to_answer(
        EngineMode mode, bool loads, bool networkDown)
    {
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet);
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure);
        await using var router = new RoutingTranscriber(() => mode, local, cloud, new ManualTimeProvider());
        if (networkDown) router.Fallback.OnNetworkAvailabilityChanged(isAvailable: false);

        (await router.WarmUpAsync(CancellationToken.None)).ShouldBe(loads);
        local.LoadCalls.ShouldBe(loads ? 1 : 0);
        cloud.LoadCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Dispose_frees_each_engine_exactly_once()
    {
        var local = new ScriptedTranscriber();
        var cloud = new ScriptedTranscriber();

        await new RoutingTranscriber(() => EngineMode.AzureWithParakeetFallback, local, cloud).DisposeAsync();

        local.DisposeCalls.ShouldBe(1);
        cloud.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_mode_from_a_newer_settings_file_stays_on_the_machine()
    {
        var local = new ScriptedTranscriber(TranscriberKind.Parakeet).Returns("local");
        var cloud = new ScriptedTranscriber(TranscriberKind.Azure).Returns("cloud");
        await using var router = new RoutingTranscriber(() => (EngineMode)42, local, cloud);

        (await router.TranscribeAsync(Audio, [], CancellationToken.None)).ShouldBe("local");
        EngineStatus.LoadsOfflineAtStartup((EngineMode)42).ShouldBeTrue();
    }
}

/// <summary>The one answer to "can the chosen engine transcribe?".</summary>
public sealed class EngineStatusTests
{
    [Theory]
    [InlineData(EngineMode.Parakeet, true, false, false, EngineReadiness.Ready)]
    [InlineData(EngineMode.Parakeet, false, true, true, EngineReadiness.NoModel)]
    [InlineData(EngineMode.Azure, false, true, true, EngineReadiness.Ready)]
    [InlineData(EngineMode.Azure, true, false, true, EngineReadiness.NotConfigured)]
    [InlineData(EngineMode.Azure, true, false, false, EngineReadiness.NotConfigured)]
    [InlineData(EngineMode.Azure, true, true, false, EngineReadiness.NoApiKey)]
    [InlineData(EngineMode.AzureWithParakeetFallback, false, true, true, EngineReadiness.Ready)]
    [InlineData(EngineMode.AzureWithParakeetFallback, true, false, false, EngineReadiness.NotConfigured)]
    [InlineData(EngineMode.AzureWithParakeetFallback, true, true, false, EngineReadiness.NoApiKey)]
    public void Evaluate_reports_the_first_thing_in_the_way(
        EngineMode mode, bool model, bool configured, bool key, EngineReadiness expected) =>
        EngineStatus.Evaluate(mode, model, configured, key).ShouldBe(expected);

    [Theory]
    [InlineData(EngineMode.Parakeet, false, true)]
    [InlineData(EngineMode.Parakeet, true, true)]
    [InlineData(EngineMode.AzureWithParakeetFallback, false, false)]
    [InlineData(EngineMode.AzureWithParakeetFallback, true, true)]
    [InlineData(EngineMode.Azure, true, false)]
    public void Parakeet_is_kept_loaded_only_where_it_is_the_engine_or_asked_for(
        EngineMode mode, bool keepWarm, bool expected) =>
        EngineStatus.KeepsOfflineLoaded(mode, keepWarm).ShouldBe(expected);

    [Fact]
    public void Only_Parakeet_mode_loads_at_startup()
    {
        EngineStatus.LoadsOfflineAtStartup(EngineMode.Parakeet).ShouldBeTrue();
        EngineStatus.LoadsOfflineAtStartup(EngineMode.Azure).ShouldBeFalse();
        EngineStatus.LoadsOfflineAtStartup(EngineMode.AzureWithParakeetFallback).ShouldBeFalse();
    }

    [Fact]
    public void The_new_settings_default_to_today_s_behaviour()
    {
        var defaults = new SettingsData();

        defaults.Engine.ShouldBe(EngineMode.Parakeet);
        defaults.KeepOfflineEngineWarm.ShouldBeFalse();
        defaults.UnloadOfflineAfterMinutes.ShouldBe(10);
        defaults.ParakeetThreads.ShouldBe(0);
    }

    [Fact]
    public void The_engine_settings_survive_a_save_and_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"murmur-engine-{Guid.NewGuid():N}", "settings.json");
        try
        {
            using (var settings = new AppSettings(path))
            {
                settings.Update(settings.Data with
                {
                    Engine = EngineMode.AzureWithParakeetFallback,
                    KeepOfflineEngineWarm = true,
                    UnloadOfflineAfterMinutes = 3,
                    ParakeetThreads = 2,
                });
            }

            using var reloaded = new AppSettings(path);
            reloaded.Data.Engine.ShouldBe(EngineMode.AzureWithParakeetFallback);
            reloaded.Data.KeepOfflineEngineWarm.ShouldBeTrue();
            reloaded.Data.UnloadOfflineAfterMinutes.ShouldBe(3);
            reloaded.Data.ParakeetThreads.ShouldBe(2);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Transient_means_network_timeout_rate_limit_and_server_error_only()
    {
        var transient = Enum.GetValues<TranscriptionFailure>().Where(TranscriptionException.IsTransientFailure);

        transient.ShouldBe(
            [
                TranscriptionFailure.Network,
                TranscriptionFailure.Timeout,
                TranscriptionFailure.RateLimited,
                TranscriptionFailure.ServerError,
            ],
            ignoreOrder: true);
    }
}
