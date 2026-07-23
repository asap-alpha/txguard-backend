namespace TxGuard.Domain.Enums;

/// <summary>Kind of financial operation. SRS §1.3 in-scope types.</summary>
public enum TransactionType
{
    Transfer,
    BillPayment,

    /// <summary>
    /// Returns the funds of an already-Completed transaction. Modelled as a normal
    /// payment in the opposite direction (original recipient → original sender), so it
    /// inherits the same durable guarantees: retries, and saga compensation if the
    /// return leg fails after the pull-back succeeded.
    /// </summary>
    Refund
}
