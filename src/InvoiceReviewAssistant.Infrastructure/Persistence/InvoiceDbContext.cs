using InvoiceReviewAssistant.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InvoiceReviewAssistant.Infrastructure.Persistence;

public sealed class InvoiceDbContext(DbContextOptions<InvoiceDbContext> options) : DbContext(options)
{
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
    public DbSet<InvoiceDocumentEntity> InvoiceDocuments => Set<InvoiceDocumentEntity>();
    public DbSet<InvoiceFieldMetadataEntity> InvoiceFieldMetadata => Set<InvoiceFieldMetadataEntity>();
    public DbSet<FieldCorrectionEntity> FieldCorrections => Set<FieldCorrectionEntity>();
    public DbSet<ValidationRunEntity> ValidationRuns => Set<ValidationRunEntity>();
    public DbSet<ValidationResultEntity> ValidationResults => Set<ValidationResultEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(SqlitePragmaConnectionInterceptor.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var invoices = modelBuilder.Entity<InvoiceEntity>();
        invoices.ToTable("Invoices");
        invoices.HasKey(entity => entity.Id);
        invoices.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        invoices.Property(entity => entity.DraftVersion).IsConcurrencyToken();
        invoices.Property(entity => entity.CreatedAtUtc).HasConversion(SqliteConverters.UtcDateTime);
        invoices.Property(entity => entity.UpdatedAtUtc).HasConversion(SqliteConverters.UtcDateTime);
        invoices.Property(entity => entity.InvoiceDate).HasConversion(SqliteConverters.NullableDateOnly);
        invoices.Property(entity => entity.DueDate).HasConversion(SqliteConverters.NullableDateOnly);
        invoices.Property(entity => entity.Subtotal).HasConversion(SqliteConverters.NullableDecimal);
        invoices.Property(entity => entity.TaxAmount).HasConversion(SqliteConverters.NullableDecimal);
        invoices.Property(entity => entity.Total).HasConversion(SqliteConverters.NullableDecimal);
        invoices.Property(entity => entity.ProcessingFailedAtUtc).HasConversion(SqliteConverters.NullableUtcDateTime);
        invoices.Property(entity => entity.DecidedAtUtc).HasConversion(SqliteConverters.NullableUtcDateTime);
        invoices.HasIndex(entity => new { entity.Status, entity.UpdatedAtUtc }).IsDescending(false, true);
        invoices.HasIndex(entity => entity.UpdatedAtUtc).IsDescending();
        invoices.HasIndex(entity => new { entity.NormalizedSupplierName, entity.NormalizedInvoiceNumber });

        var documents = modelBuilder.Entity<InvoiceDocumentEntity>();
        documents.ToTable("InvoiceDocuments");
        documents.HasKey(entity => entity.InvoiceId);
        documents.Property(entity => entity.StorageKey).HasMaxLength(512).IsRequired();
        documents.HasIndex(entity => entity.StorageKey).IsUnique();
        documents.HasOne(entity => entity.Invoice).WithOne(entity => entity.Document)
            .HasForeignKey<InvoiceDocumentEntity>(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        var metadata = modelBuilder.Entity<InvoiceFieldMetadataEntity>();
        metadata.ToTable("InvoiceFieldMetadata");
        metadata.HasKey(entity => new { entity.InvoiceId, entity.FieldKey });
        metadata.Property(entity => entity.LastCorrectedAtUtc).HasConversion(SqliteConverters.NullableUtcDateTime);
        metadata.HasOne(entity => entity.Invoice).WithMany(entity => entity.FieldMetadata)
            .HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        var corrections = modelBuilder.Entity<FieldCorrectionEntity>();
        corrections.ToTable("FieldCorrections");
        corrections.HasKey(entity => entity.Id);
        corrections.Property(entity => entity.Id).ValueGeneratedOnAdd();
        corrections.Property(entity => entity.OccurredAtUtc).HasConversion(SqliteConverters.UtcDateTime);
        corrections.HasOne(entity => entity.Invoice).WithMany(entity => entity.FieldCorrections)
            .HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        corrections.HasOne(entity => entity.AuditEvent).WithMany(entity => entity.FieldCorrections)
            .HasForeignKey(entity => entity.AuditEventId).OnDelete(DeleteBehavior.Restrict);

        var validationRuns = modelBuilder.Entity<ValidationRunEntity>();
        validationRuns.ToTable("ValidationRuns");
        validationRuns.HasKey(entity => entity.Id);
        validationRuns.Property(entity => entity.ValidatedAtUtc).HasConversion(SqliteConverters.UtcDateTime);
        validationRuns.HasIndex(entity => new { entity.InvoiceId, entity.ValidatedAtUtc }).IsDescending(false, true);
        validationRuns.HasOne(entity => entity.Invoice).WithMany(entity => entity.ValidationRuns)
            .HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        var validationResults = modelBuilder.Entity<ValidationResultEntity>();
        validationResults.ToTable("ValidationResults");
        validationResults.HasKey(entity => entity.Id);
        validationResults.Property(entity => entity.Id).ValueGeneratedOnAdd();
        validationResults.HasOne(entity => entity.ValidationRun).WithMany(entity => entity.Results)
            .HasForeignKey(entity => entity.ValidationRunId).OnDelete(DeleteBehavior.Cascade);

        var audit = modelBuilder.Entity<AuditEventEntity>();
        audit.ToTable("AuditEvents");
        audit.HasKey(entity => entity.Id);
        audit.Property(entity => entity.Id).ValueGeneratedOnAdd();
        audit.Property(entity => entity.OccurredAtUtc).HasConversion(SqliteConverters.UtcDateTime);
        audit.HasIndex(entity => new { entity.InvoiceId, entity.OccurredAtUtc, entity.Id });
        audit.HasOne(entity => entity.Invoice).WithMany(entity => entity.AuditEvents)
            .HasForeignKey(entity => entity.InvoiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SqlitePragmaConnectionInterceptor : DbConnectionInterceptor
{
    public static SqlitePragmaConnectionInterceptor Instance { get; } = new();

    public override void ConnectionOpened(System.Data.Common.DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            SqliteConnectionConfiguration.Apply(sqliteConnection);
        }
    }

    public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqliteConnection)
        {
            await SqliteConnectionConfiguration.ApplyAsync(sqliteConnection, cancellationToken);
        }
    }
}
