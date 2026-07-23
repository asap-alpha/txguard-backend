using Temporalio.Client;
using Temporalio.Worker;
using TxGuard.Workflows;

namespace TxGuard.Api.Demo;

/// <summary>
/// Hosts the Temporal worker with a lifecycle we control, so a demo can stop the
/// worker (submitted transactions then queue durably in Temporal, making no
/// progress) and start it again (the backlog drains and every workflow resumes
/// exactly where it left off).
///
/// Replaces <c>AddHostedTemporalWorker</c>, whose BackgroundService cannot be
/// restarted once stopped. A fresh <see cref="TemporalWorker"/> is built on each
/// start. Holding one activities instance for the worker's lifetime is safe:
/// <c>EfTransactionStore</c> creates a DbContext per call from the factory rather
/// than capturing one.
/// </summary>
public sealed class ControllableWorkerHost : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ControllableWorkerHost> _logger;

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private IServiceScope? _scope;

    public ControllableWorkerHost(IServiceProvider services, ILogger<ControllableWorkerHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>True while the worker is polling the task queue.</summary>
    public bool IsRunning => _runTask is { IsCompleted: false };

    public Task StartAsync(CancellationToken ct) => StartWorkerAsync(ct);

    public Task StopAsync(CancellationToken ct) => StopWorkerAsync(ct);

    public async Task StartWorkerAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        await StopWorkerAsync(ct);   // clear out a previous, finished run

        _scope = _services.CreateScope();
        var sp = _scope.ServiceProvider;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Everything runs inside the background task: connecting can fail (Temporal not
        // up yet) and that must not take the whole API down with it — the panel can
        // simply start the worker again once Temporal is reachable.
        _runTask = Task.Run(async () =>
        {
            TemporalWorker? worker = null;
            try
            {
                // AddTemporalClient registers a *lazy* client; a worker needs a connected one.
                var client = sp.GetRequiredService<ITemporalClient>();
                await client.Connection.ConnectAsync();

                var activities = ActivatorUtilities.CreateInstance<TransactionActivities>(sp);
                worker = new TemporalWorker(client,
                    new TemporalWorkerOptions(TxGuardConstants.TaskQueue)
                        .AddAllActivities(activities)
                        .AddWorkflow<TransactionWorkflow>());

                _logger.LogInformation("Temporal worker started on queue {Queue}", TxGuardConstants.TaskQueue);
                await worker.ExecuteAsync(token);
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Temporal worker stopped unexpectedly");
            }
            finally
            {
                worker?.Dispose();
            }
        }, CancellationToken.None);
    }

    public async Task StopWorkerAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_runTask is not null)
        {
            try { await _runTask; } catch { /* already logged */ }
            _logger.LogInformation("Temporal worker stopped");
        }

        _cts?.Dispose();
        _cts = null;
        _runTask = null;
        _scope?.Dispose();
        _scope = null;
    }
}
