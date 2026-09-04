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
        public Task SendEmailConfirmationAsync(string toEmail, string url, CancellationToken ct = default)
            => Record(AccountEmailKind.EmailConfirmation, toEmail, url);
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
        api.Enqueue(new AccountEmailJob(AccountEmailKind.EmailConfirmation, "confirm@x.test", "https://x/confirm"));
        api.Enqueue(new AccountEmailJob(AccountEmailKind.AlreadyRegistered, "dup@x.test", "https://x/login"));

        await DrainAsync(queue, mailer);

        // FIFO single-reader, so the order is the enqueue order — and each landed on the RIGHT method with
        // its recipient + url intact (a swapped dispatch case fails this).
        Assert.Collection(mailer.Records,
            s => Assert.Equal(new Sent(AccountEmailKind.PasswordReset, "reset@x.test", "https://x/reset"), s),
            s => Assert.Equal(new Sent(AccountEmailKind.EmailConfirmation, "confirm@x.test", "https://x/confirm"), s),
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
        api.Enqueue(new AccountEmailJob(AccountEmailKind.EmailConfirmation, "ok@x.test", "https://x/confirm"));

        await DrainAsync(queue, mailer);

        // The good one still went; the failure was swallowed (logged) rather than propagated.
        var only = Assert.Single(mailer.Records);
        Assert.Equal(new Sent(AccountEmailKind.EmailConfirmation, "ok@x.test", "https://x/confirm"), only);
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
}
