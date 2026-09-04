using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The background email pipeline: pages enqueue an <see cref="AccountEmailJob"/>, the
/// <see cref="AccountEmailWorker"/> drains the queue and dispatches each to the matching
/// <see cref="IAccountMailer"/> method — off the request thread, which is what makes outbound-mail timing
/// uniform (no send blocks a response) and keeps a bad send from stalling the rest.
/// </summary>
public class AccountEmailQueueTests
{
    private sealed record Sent(AccountEmailKind Kind, string ToEmail, string Url);

    /// <summary>Records what the worker asked it to send, signals when it has seen enough, and can be told
    /// to throw for one recipient (to prove a bad send doesn't kill the worker).</summary>
    private sealed class RecordingMailer(int expected, string? throwFor = null) : IAccountMailer
    {
        private readonly ConcurrentQueue<Sent> _sent = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<Sent> Records => _sent.ToList();
        public Task Reached => _done.Task;

        public Task SendPasswordResetAsync(string toEmail, string url, CancellationToken ct = default)
            => Record(AccountEmailKind.PasswordReset, toEmail, url);
        public Task SendAccountActivationAsync(string toEmail, string url, CancellationToken ct = default)
            => Record(AccountEmailKind.Activation, toEmail, url);
        public Task SendAlreadyRegisteredAsync(string toEmail, string url, CancellationToken ct = default)
            => Record(AccountEmailKind.AlreadyRegistered, toEmail, url);

        private Task Record(AccountEmailKind kind, string toEmail, string url)
        {
            if (toEmail == throwFor)
            {
                throw new InvalidOperationException("relay is down");
            }
            _sent.Enqueue(new Sent(kind, toEmail, url));
            if (_sent.Count >= expected)
            {
                _done.TrySetResult();
            }
            return Task.CompletedTask;
        }
    }

    private static async Task DrainAsync(AccountEmailQueue queue, RecordingMailer mailer)
    {
        var worker = new AccountEmailWorker(queue, mailer, NullLogger<AccountEmailWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await mailer.Reached.WaitAsync(TimeSpan.FromSeconds(10)); // guard against a hang
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Each_kind_dispatches_to_the_matching_mailer_method_in_order()
    {
        var mailer = new RecordingMailer(expected: 3);
        var queue = new AccountEmailQueue(NullLogger<AccountEmailQueue>.Instance);
        IAccountEmailQueue api = queue;

        api.Enqueue(new AccountEmailJob(AccountEmailKind.PasswordReset, "reset@x.test", "https://x/reset"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.Activation, "confirm@x.test", "https://x/confirm"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.AlreadyRegistered, "dup@x.test", "https://x/login"));

        await DrainAsync(queue, mailer);

        // FIFO single-reader, so the order is the enqueue order — and each landed on the RIGHT method with
        // its recipient + url intact (a swapped dispatch case fails this).
        Assert.Collection(mailer.Records,
            s => Assert.Equal(new Sent(AccountEmailKind.PasswordReset, "reset@x.test", "https://x/reset"), s),
            s => Assert.Equal(new Sent(AccountEmailKind.Activation, "confirm@x.test", "https://x/confirm"), s),
            s => Assert.Equal(new Sent(AccountEmailKind.AlreadyRegistered, "dup@x.test", "https://x/login"), s));
    }

    [Fact]
    public async Task A_failing_send_is_logged_and_the_worker_carries_on()
    {
        // The whole point of the pump: one bad send (a down relay) must not stall or kill the queue.
        var mailer = new RecordingMailer(expected: 1, throwFor: "boom@x.test");
        var queue = new AccountEmailQueue(NullLogger<AccountEmailQueue>.Instance);
        IAccountEmailQueue api = queue;

        api.Enqueue(new AccountEmailJob(AccountEmailKind.PasswordReset, "boom@x.test", "https://x/reset"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.Activation, "ok@x.test", "https://x/confirm"));

        await DrainAsync(queue, mailer);

        // The good one still went; the failure was swallowed (logged) rather than propagated.
        var only = Assert.Single(mailer.Records);
        Assert.Equal(new Sent(AccountEmailKind.Activation, "ok@x.test", "https://x/confirm"), only);
    }

    /// <summary>Blocks (respecting the token) on its FIRST send so the worker loop is stuck on it and later
    /// jobs stay buffered — then records the rest, so a test can prove StopAsync drains the buffer.</summary>
    private sealed class BlockingFirstMailer : IAccountMailer
    {
        private int _calls;
        private readonly ConcurrentQueue<string> _sent = new();
        public IReadOnlyList<string> SentTo => _sent.ToList();
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendPasswordResetAsync(string toEmail, string url, CancellationToken ct = default) => Handle(toEmail, ct);
        public Task SendAccountActivationAsync(string toEmail, string url, CancellationToken ct = default) => Handle(toEmail, ct);
        public Task SendAlreadyRegisteredAsync(string toEmail, string url, CancellationToken ct = default) => Handle(toEmail, ct);

        private async Task Handle(string toEmail, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // hold the loop until shutdown cancels ct
            }
            // Honour the token like a real relay (MailKit throws on a cancelled ct). This is what makes the
            // test distinguish the fix from the bug: the OLD loop dispatched buffered jobs under the CANCELLED
            // stopping token (so they'd throw here and be abandoned, not sent), while the drain dispatches
            // them under a fresh token (so they send).
            ct.ThrowIfCancellationRequested();
            _sent.Enqueue(toEmail);
        }
    }

    [Fact]
    public async Task Buffered_jobs_are_drained_on_shutdown_not_silently_dropped()
    {
        // The re-gate found the loop consumed buffered jobs (abandoning them) before StopAsync could drain.
        // Here the first send blocks so the loop is stuck on it and the other two stay BUFFERED; on shutdown
        // the blocked one is abandoned and StopAsync must SEND the two buffered — not drop them.
        var mailer = new BlockingFirstMailer();
        var queue = new AccountEmailQueue(NullLogger<AccountEmailQueue>.Instance);
        IAccountEmailQueue api = queue;
        var worker = new AccountEmailWorker(queue, mailer, NullLogger<AccountEmailWorker>.Instance);

        api.Enqueue(new AccountEmailJob(AccountEmailKind.PasswordReset, "blocks@x.test", "u"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.Activation, "buffered1@x.test", "u"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.AlreadyRegistered, "buffered2@x.test", "u"));

        await worker.StartAsync(CancellationToken.None);
        await mailer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the loop is now stuck on job 1
        await worker.StopAsync(CancellationToken.None); // cancels the loop; the drain must send 2 and 3

        Assert.Contains("buffered1@x.test", mailer.SentTo);
        Assert.Contains("buffered2@x.test", mailer.SentTo);
        Assert.DoesNotContain("blocks@x.test", mailer.SentTo); // the in-flight one was abandoned, as intended
    }

    [Fact]
    public void Enqueue_never_throws_even_past_capacity()
    {
        // Best-effort and mid-request: with no worker draining, filling well past the bound just drops the
        // overflow (logged), never throws onto the caller.
        var queue = new AccountEmailQueue(NullLogger<AccountEmailQueue>.Instance);
        IAccountEmailQueue api = queue;

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 2000; i++) // > the 1000 bound
            {
                api.Enqueue(new AccountEmailJob(AccountEmailKind.PasswordReset, $"u{i}@x.test", "https://x/reset"));
            }
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task Stopping_the_worker_closes_intake_so_a_later_enqueue_is_refused()
    {
        // Pins the WIRING, not just CompleteWriter itself: the worker's StopAsync must complete the channel,
        // so a mail enqueued after shutdown is refused (a logged drop) rather than silently written to a
        // channel with no reader left — the residual its sibling ErrorLogWriter closes the same way. Removing
        // queue.CompleteWriter() from StopAsync makes the enqueue succeed and this fail. Deterministic:
        // StopAsync completes the channel synchronously before it awaits the base drain, and draining an
        // already-empty channel is immediate.
        var queue = new AccountEmailQueue(NullLogger<AccountEmailQueue>.Instance);
        IAccountEmailQueue api = queue;
        var worker = new AccountEmailWorker(queue, new RecordingMailer(expected: 1), NullLogger<AccountEmailWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        api.Enqueue(new AccountEmailJob(AccountEmailKind.PasswordReset, "late@x.test", "https://x/reset"));

        Assert.False(queue.Reader.TryRead(out _)); // intake closed by StopAsync → refused, not buffered
    }
}
