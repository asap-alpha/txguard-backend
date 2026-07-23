using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TxGuard.Domain;
using TxGuard.Domain.Abstractions;
using TxGuard.Domain.Enums;
using TxGuard.Domain.Transactions;

namespace TxGuard.Infrastructure.Persistence;

/// <summary>
/// EF Core / Postgres implementation of the read-model store. Each method resolves
/// a fresh <see cref="TxGuardDbContext"/> so it is safe to call from Temporal
/// activities (which may run concurrently and be retried).
/// </summary>
public sealed class EfTransactionStore : ITransactionStore
{
    private readonly IDbContextFactory<TxGuardDbContext> _factory;
    private readonly ITransactionNotifier _notifier;

    public EfTransactionStore(IDbContextFactory<TxGuardDbContext> factory, ITransactionNotifier notifier)
    {
        _factory = factory;
        _notifier = notifier;
    }

    public async Task RecordCreatedAsync(TransactionRequest r, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        if (await db.Transactions.AnyAsync(x => x.TransactionId == r.TransactionId, ct))
            return; // idempotent

        var now = r.CreatedAtUtc;
        db.Transactions.Add(new TransactionEntity
        {
            TransactionId = r.TransactionId,
            IdempotencyKey = r.IdempotencyKey,
            SenderAccountId = r.Sender.AccountId,
            SenderName = r.Sender.Name,
            SenderNumber = r.Sender.AccountNumber,
            SenderProvider = r.Sender.Provider,
            RecipientAccountId = r.Recipient.AccountId,
            RecipientName = r.Recipient.Name,
            RecipientNumber = r.Recipient.AccountNumber,
            RecipientProvider = r.Recipient.Provider,
            AmountMinor = r.AmountMinor,
            Currency = r.Currency,
            Type = r.Type,
            Reference = r.Reference,
            State = TransactionState.Pending,
            Retries = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        db.AuditEvents.Add(NewEvent(r.TransactionId, AuditEventType.TransactionCreated,
            newState: TransactionState.Pending, previousState: null,
            details: $"Transaction created for {new Money(r.AmountMinor, r.Currency)}",
            data: new { SenderProvider = r.Sender.Provider, RecipientProvider = r.Recipient.Provider }));
        await db.SaveChangesAsync(ct);
        await _notifier.TransactionChangedAsync(r.TransactionId, ct);
    }

    public async Task RecordFraudAsync(string transactionId, FraudAssessment a, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tx = await db.Transactions.FirstOrDefaultAsync(x => x.TransactionId == transactionId, ct);
        if (tx is null) return;

        tx.RiskScore = a.Score;
        tx.RiskLevel = a.Level;
        tx.FraudModelVersion = a.ModelVersion;
        tx.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditEvents.Add(NewEvent(transactionId, AuditEventType.FraudScored,
            newState: tx.State, previousState: tx.State,
            details: $"Risk {a.Level} ({a.Score:0.00}) via {a.ModelVersion}",
            data: new { a.Score, Level = a.Level.ToString(), a.ModelVersion, a.Features }));
        await db.SaveChangesAsync(ct);
        await _notifier.TransactionChangedAsync(transactionId, ct);
    }

    public async Task TransitionAsync(string transactionId, TransactionState newState, AuditEventType eventType,
        string? failureReason = null, int? retries = null, object? data = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tx = await db.Transactions.FirstOrDefaultAsync(x => x.TransactionId == transactionId, ct);
        if (tx is null) return;

        var previous = tx.State;
        tx.State = newState;
        if (failureReason is not null) tx.FailureReason = failureReason;
        if (retries is not null) tx.Retries = retries.Value;
        tx.UpdatedAtUtc = DateTime.UtcNow;
        db.AuditEvents.Add(NewEvent(transactionId, eventType, newState, previous, failureReason, data));
        await db.SaveChangesAsync(ct);
        await _notifier.TransactionChangedAsync(transactionId, ct);
    }

    public async Task AppendEventAsync(string transactionId, AuditEventType type,
        string? details = null, object? data = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tx = await db.Transactions.FirstOrDefaultAsync(x => x.TransactionId == transactionId, ct);
        var state = tx?.State;
        if (tx is not null && type == AuditEventType.RetryScheduled)
        {
            tx.Retries += 1;
            tx.UpdatedAtUtc = DateTime.UtcNow;
        }
        db.AuditEvents.Add(NewEvent(transactionId, type, state, state, details, data));
        await db.SaveChangesAsync(ct);
        await _notifier.TransactionChangedAsync(transactionId, ct);
    }

    private static AuditEventEntity NewEvent(string txId, AuditEventType type,
        TransactionState? newState, TransactionState? previousState, string? details, object? data)
        => new()
        {
            TransactionId = txId,
            EventType = type,
            PreviousState = previousState,
            NewState = newState,
            Details = details,
            DataJson = data is null ? null : JsonSerializer.Serialize(data),
            TimestampUtc = DateTime.UtcNow,
        };
}
