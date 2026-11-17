using System.Numerics;
using System.Text.Json;
using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace AcceptCrypto.Infrastructure.Persistence;

/// <summary>
/// EF Core context for PostgreSQL. Amounts are <c>numeric(78,0)</c> in token base units (NFR-1); the invoice
/// row carries an explicit <c>version</c> concurrency token that is bumped on every change of the aggregate,
/// including changes to its transfers only (NFR-3, docs/DECISIONS.md D12).
/// </summary>
public sealed class AcceptCryptoDbContext(DbContextOptions<AcceptCryptoDbContext> options, IOptions<PersistenceOptions> persistenceOptions)
    : DbContext(options)
{
    /// <summary>Sequence of BIP32 derivation indexes / invoice numbers.</summary>
    public const string DerivationIndexSequence = "invoice_derivation_index_seq";

    internal const string VersionProperty = "Version";
    internal const string ChainIdProperty = "ChainId";
    internal const string InvoiceIdProperty = "InvoiceId";

    private readonly long _chainId = persistenceOptions?.Value.ChainId ?? throw new ArgumentNullException(nameof(persistenceOptions));

    /// <summary>Invoices with their observed transfers (owned).</summary>
    public DbSet<Invoice> Invoices => Set<Invoice>();

    /// <summary>Unmatched payments (FR-11a).</summary>
    public DbSet<UnmatchedPayment> UnmatchedPayments => Set<UnmatchedPayment>();

    /// <summary>Webhook outbox (NFR-2).</summary>
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    /// <summary>Scanner checkpoints (FR-9).</summary>
    public DbSet<ScannerCheckpoint> ScannerCheckpoints => Set<ScannerCheckpoint>();

    /// <summary>Idempotency keys (FR-2).</summary>
    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareAggregates();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareAggregates();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<UsdcAmount>().HaveConversion<UsdcAmountConverter>().HaveColumnType("numeric(78,0)");
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasSequence<long>(DerivationIndexSequence).StartsAt(0).HasMin(0).HasMax(Invoice.MaxDerivationIndex);

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices", t =>
            {
                t.HasCheckConstraint("ck_invoices_amount_positive", "amount > 0");
                t.HasCheckConstraint("ck_invoices_derivation_index", $"derivation_index BETWEEN 0 AND {Invoice.MaxDerivationIndex}");
            });
            b.HasKey(i => i.Id);
            b.Property(i => i.Id).ValueGeneratedNever();
            b.Property(i => i.Status).HasConversion(new ValueConverter<InvoiceStatus, string>(
                s => OutboxEventFactory.StatusName(s),
                s => ParseStatus(s))).HasMaxLength(16);
            b.Property(i => i.ExternalOrderId).HasMaxLength(256);
            b.Property(i => i.IdempotencyKey).HasMaxLength(256);
            b.Property(i => i.DepositAddress).HasMaxLength(42);
            b.Property(i => i.PublicToken).HasMaxLength(64);
            b.Property(i => i.Metadata)
                .HasColumnType("jsonb")
                .HasConversion(new MetadataConverter(), new MetadataComparer());
            b.Property<int>(VersionProperty).IsConcurrencyToken();

            b.HasIndex(i => i.DepositAddress).IsUnique();
            b.HasIndex(i => i.DerivationIndex).IsUnique();
            b.HasIndex(i => i.PublicToken).IsUnique();
            b.HasIndex(i => i.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            b.HasIndex(i => new { i.CreatedAt, i.Id }).IsDescending();
            b.HasIndex(i => new { i.Status, i.ExpiresAt });

            b.Ignore(i => i.ConfirmedTotal);
            b.Ignore(i => i.ConfirmedOnTimeTotal);
            b.Ignore(i => i.UnconfirmedTotal);
            b.Ignore(i => i.RemainingAmount);
            b.Ignore(i => i.ExcessAmount);

            b.OwnsMany(i => i.Transfers, t =>
            {
                t.ToTable("observed_transfers", tb => tb.HasCheckConstraint("ck_observed_transfers_amount_positive", "amount > 0"));
                t.WithOwner().HasForeignKey(InvoiceIdProperty);
                t.Property<long>("RowId").HasColumnName("id").UseIdentityAlwaysColumn();
                t.HasKey("RowId");
                t.Ignore(x => x.Id);
                t.Property<long>(ChainIdProperty);
                t.Property(x => x.TxHash).HasMaxLength(66);
                t.Property(x => x.BlockHash).HasMaxLength(66);
                t.Property(x => x.From).HasColumnName("from_address").HasMaxLength(42);
                t.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
                t.HasIndex(ChainIdProperty, nameof(ObservedTransfer.TxHash), nameof(ObservedTransfer.LogIndex)).IsUnique();
                t.HasIndex(nameof(ObservedTransfer.State), nameof(ObservedTransfer.BlockNumber));
            });
            b.Navigation(i => i.Transfers).HasField("_transfers").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<UnmatchedPayment>(b =>
        {
            b.ToTable("unmatched_payments");
            b.HasKey(u => u.Id);
            b.Property(u => u.Id).UseIdentityAlwaysColumn();
            b.Property(u => u.TxHash).HasMaxLength(66);
            b.Property(u => u.BlockHash).HasMaxLength(66);
            b.Property(u => u.FromAddress).HasMaxLength(42);
            b.Property(u => u.ToAddress).HasMaxLength(42);
            b.Property(u => u.Reason).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(u => new { u.ChainId, u.TxHash, u.LogIndex }).IsUnique();
            b.HasIndex(u => u.ToAddress);
        });

        modelBuilder.Entity<OutboxEvent>(b =>
        {
            b.ToTable("outbox_events");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).UseIdentityAlwaysColumn();
            b.Property(e => e.Type).HasMaxLength(64);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(e => e.LastError).HasMaxLength(2048);
            b.Property<uint>(VersionProperty).IsRowVersion(); // PostgreSQL xmin
            b.HasIndex(e => e.EventId).IsUnique();
            b.HasIndex(e => e.InvoiceId);
            b.HasIndex(e => new { e.Status, e.NextAttemptAt });
            b.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(e => e.DeliveryAttempts).WithOne().HasForeignKey("OutboxEventId").OnDelete(DeleteBehavior.Cascade);
            b.Navigation(e => e.DeliveryAttempts).HasField("_deliveryAttempts").UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<WebhookDeliveryAttempt>(b =>
        {
            b.ToTable("webhook_delivery_attempts");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).UseIdentityAlwaysColumn();
            b.Property(a => a.Error).HasMaxLength(2048);
        });

        modelBuilder.Entity<ScannerCheckpoint>(b =>
        {
            b.ToTable("scanner_checkpoints");
            b.HasKey(c => c.ChainId);
            b.Property(c => c.ChainId).ValueGeneratedNever();
            b.Property(c => c.LastScannedBlockHash).HasMaxLength(66);
        });

        modelBuilder.Entity<IdempotencyKeyRecord>(b =>
        {
            b.ToTable("idempotency_keys");
            b.HasKey(k => k.Key);
            b.Property(k => k.Key).HasMaxLength(256);
            b.Property(k => k.RequestFingerprint).HasMaxLength(128);
            b.HasOne<Invoice>().WithMany().HasForeignKey(k => k.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static InvoiceStatus ParseStatus(string value) =>
        Enum.GetValues<InvoiceStatus>().First(s => OutboxEventFactory.StatusName(s) == value);

    /// <summary>
    /// Stamps the chain ID on new transfers and bumps the version of every invoice whose aggregate changed,
    /// so two writers of the same invoice always conflict, even when only its transfers changed.
    /// </summary>
    private void PrepareAggregates()
    {
        ChangeTracker.DetectChanges();

        var touchedInvoices = new HashSet<Guid>();
        foreach (var entry in ChangeTracker.Entries<ObservedTransfer>())
        {
            if (entry.State == EntityState.Added && entry.Property<long>(ChainIdProperty).CurrentValue == 0)
            {
                entry.Property<long>(ChainIdProperty).CurrentValue = _chainId;
            }

            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                touchedInvoices.Add(entry.Property<Guid>(InvoiceIdProperty).CurrentValue);
            }
        }

        foreach (var entry in ChangeTracker.Entries<Invoice>())
        {
            if (entry.State == EntityState.Modified || (entry.State == EntityState.Unchanged && touchedInvoices.Contains(entry.Entity.Id)))
            {
                var version = entry.Property<int>(VersionProperty);
                version.CurrentValue = version.OriginalValue + 1;
            }
        }
    }

    private sealed class UsdcAmountConverter() : ValueConverter<UsdcAmount, BigInteger>(a => a.BaseUnits, v => UsdcAmount.FromBaseUnits(v));

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

    private sealed class MetadataConverter() : ValueConverter<IReadOnlyDictionary<string, string>, string>(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, string>());

    private sealed class MetadataComparer() : ValueComparer<IReadOnlyDictionary<string, string>>(
        (a, b) => a != null && b != null && a.Count == b.Count && !a.Except(b).Any(),
        v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key.GetHashCode(StringComparison.Ordinal), kv.Value.GetHashCode(StringComparison.Ordinal))),
        v => new Dictionary<string, string>(v));
}
