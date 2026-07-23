using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TxGuard.Api.Auth;
using TxGuard.Api.Contracts;
using TxGuard.Api.Demo;
using TxGuard.Infrastructure.Configuration;

namespace TxGuard.Api.Controllers;

/// <summary>
/// Failure-injection controls that drive the dashboard's demo panel: force fraud
/// review, force retries / saga compensation, sever the database, and stop or start
/// the Temporal worker.
///
/// Every action 404s unless demo mode is on: automatically in the Development
/// environment, or explicitly via <c>TxGuard:EnableDemoControls=true</c> for a
/// deployed demo. This surface can sever the database and stop the worker, so it must
/// never be left reachable in a real production deployment.
/// </summary>
[ApiController]
[Authorize(Roles = Roles.Admin)]   // demo/chaos controls are Admin-only, on top of the demo-mode gate
[Route("api/v1/demo")]
public sealed class DemoController : ControllerBase, IActionFilter
{
    private readonly IRuntimeSettings _settings;
    private readonly DbChaosService _db;
    private readonly ControllableWorkerHost _worker;
    private readonly bool _demoEnabled;

    public DemoController(
        IRuntimeSettings settings,
        DbChaosService db,
        ControllableWorkerHost worker,
        IHostEnvironment env,
        IConfiguration config)
    {
        _settings = settings;
        _db = db;
        _worker = worker;

        // On in Development, or wherever the operator explicitly opts in. Keeping the flag
        // separate from ASPNETCORE_ENVIRONMENT means a deployed demo can expose these
        // controls without also flipping on Swagger, verbose errors, etc.
        _demoEnabled = env.IsDevelopment()
            || config.GetValue("TxGuard:EnableDemoControls", false);
    }

    // [NonAction] keeps the API explorer / Swagger from treating these filter hooks as
    // endpoints — otherwise they have no HTTP verb and break swagger.json generation.
    [NonAction]
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!_demoEnabled)
            context.Result = NotFound();
    }

    [NonAction]
    public void OnActionExecuted(ActionExecutedContext context) { }

    // ── Current state of every control ────────────────────────────────────
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        bool dbBroken;
        try { dbBroken = await _db.IsBrokenAsync(); }
        catch { dbBroken = true; }   // can't even reach the server

        return Ok(new DemoStatusDto(
            _settings.LowRiskThreshold, _settings.HighRiskThreshold,
            _settings.DebitTransientFailureRate, _settings.CreditTransientFailureRate,
            _settings.CreditPermanentFailureRate, _settings.ReversalPermanentFailureRate,
            _settings.LatencyMs, dbBroken, _worker.IsRunning));
    }

    // ── Fraud thresholds (force FRAUD_REVIEW on demand) ───────────────────
    [HttpPost("fraud-thresholds")]
    public IActionResult SetFraudThresholds([FromBody] FraudThresholdsRequest body)
    {
        if (body.LowRiskThreshold is < 0 or > 1 || body.HighRiskThreshold is < 0 or > 1)
            return BadRequest(new ApiError("TXG-INVALID", "Thresholds must be between 0 and 1"));
        if (body.LowRiskThreshold > body.HighRiskThreshold)
            return BadRequest(new ApiError("TXG-INVALID", "Low threshold must not exceed high threshold"));

        _settings.LowRiskThreshold = body.LowRiskThreshold;
        _settings.HighRiskThreshold = body.HighRiskThreshold;
        return Ok(new { _settings.LowRiskThreshold, _settings.HighRiskThreshold });
    }

    // ── Mock banking failure rates (force retries / saga reversal) ────────
    [HttpPost("banking-rates")]
    public IActionResult SetBankingRates([FromBody] BankingRatesRequest body)
    {
        foreach (var rate in new[] { body.DebitTransientFailureRate, body.CreditTransientFailureRate,
                                     body.CreditPermanentFailureRate, body.ReversalPermanentFailureRate })
            if (rate is < 0 or > 1)
                return BadRequest(new ApiError("TXG-INVALID", "Failure rates must be between 0 and 1"));
        if (body.LatencyMs is < 0 or > 10_000)
            return BadRequest(new ApiError("TXG-INVALID", "Latency must be between 0 and 10000 ms"));

        _settings.DebitTransientFailureRate = body.DebitTransientFailureRate;
        _settings.CreditTransientFailureRate = body.CreditTransientFailureRate;
        _settings.CreditPermanentFailureRate = body.CreditPermanentFailureRate;
        _settings.ReversalPermanentFailureRate = body.ReversalPermanentFailureRate;
        _settings.LatencyMs = body.LatencyMs;
        return Ok(new
        {
            _settings.DebitTransientFailureRate,
            _settings.CreditTransientFailureRate,
            _settings.CreditPermanentFailureRate,
            _settings.ReversalPermanentFailureRate,
            _settings.LatencyMs,
        });
    }

    // ── Database outage ───────────────────────────────────────────────────
    [HttpPost("db/break")]
    public async Task<IActionResult> BreakDb()
    {
        await _db.BreakAsync();
        return Ok(new { dbBroken = true, message = "Application database is refusing connections" });
    }

    [HttpPost("db/heal")]
    public async Task<IActionResult> HealDb()
    {
        await _db.HealAsync();
        return Ok(new { dbBroken = false, message = "Application database restored" });
    }

    // ── Temporal worker ───────────────────────────────────────────────────
    [HttpPost("worker/stop")]
    public async Task<IActionResult> StopWorker()
    {
        await _worker.StopWorkerAsync();
        return Ok(new { workerRunning = false, message = "Worker stopped — new transactions will queue in Temporal" });
    }

    [HttpPost("worker/start")]
    public async Task<IActionResult> StartWorker()
    {
        await _worker.StartWorkerAsync();
        return Ok(new { workerRunning = true, message = "Worker started — queued workflows will resume" });
    }

    // ── Restore everything to configured defaults ─────────────────────────
    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        _settings.ResetToConfiguredDefaults();
        await _db.HealAsync();
        await _worker.StartWorkerAsync();
        return Ok(new { message = "All demo controls reset to configured defaults" });
    }
}
