using Microsoft.EntityFrameworkCore;
using TxGuard.Domain.Enums;

namespace TxGuard.Infrastructure.Persistence;

public class TxGuardDbContext : DbContext
{
    public TxGuardDbContext(DbContextOptions<TxGuardDbContext> options) : base(options) { }

    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TransactionEntity>(e =>
        {
            e.ToTable("transactions");
            e.HasKey(x => x.TransactionId);
            e.Property(x => x.TransactionId).HasMaxLength(64);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();   // FR-TI-003 dedup
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.CreatedAtUtc);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RiskLevel).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Currency).HasMaxLength(8);
        });

        b.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TransactionId);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.TimestampUtc);
            // Tolerant conversion: an audit row whose EventType this build doesn't know
            // (schema drift, hand-edited data) reads back as Unknown instead of throwing and
            // 500-ing the whole transaction detail view. The audit log is append-only display
            // data, so degrading one label is strictly better than failing the request.
            e.Property(x => x.EventType)
                .HasConversion(new TolerantEnumToStringConverter<AuditEventType>(AuditEventType.Unknown))
                .HasMaxLength(48);
            e.Property(x => x.PreviousState).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.NewState).HasConversion<string>().HasMaxLength(32);
            e.HasOne<TransactionEntity>()
                .WithMany(t => t.Events)
                .HasForeignKey(x => x.TransactionId);
        });

        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Hash).IsUnique();   // lookup on presented key
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Prefix).HasMaxLength(32);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.Role).HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(128);
        });
    }
}
