using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using ReportingService.Domain;

namespace ReportingService.Persistence;

public sealed class ReportingDbContext : DbContext
{
    public ReportingDbContext(DbContextOptions<ReportingDbContext> options) : base(options)
    {
    }

    public DbSet<ReportingOrder> Orders => Set<ReportingOrder>();
    public DbSet<ReportingEvent> Events => Set<ReportingEvent>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReportingOrder>(builder =>
        {
            builder.ToTable("reporting_orders");
            builder.HasKey(order => order.OrderId);
            builder.Property(order => order.Status).HasMaxLength(64).IsRequired();
            builder.Property(order => order.Currency).HasMaxLength(3).IsRequired();
            builder.Property(order => order.LastEventType).HasMaxLength(256).IsRequired();
            builder.Property(order => order.TotalAmount).HasPrecision(18, 2).IsRequired();
            builder.Property(order => order.UpdatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ReportingEvent>(builder =>
        {
            builder.ToTable("reporting_events");
            builder.HasKey(e => e.Id);
            builder.HasIndex(e => e.MessageId).IsUnique();
            builder.HasIndex(e => e.OrderId);
            builder.HasIndex(e => e.EventType);
            builder.Property(e => e.EventType).HasMaxLength(256).IsRequired();
            builder.Property(e => e.Source).HasMaxLength(256).IsRequired();
            builder.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            builder.Property(e => e.OccurredOnUtc).IsRequired();
            builder.Property(e => e.RecordedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.ToTable("reporting_processed_messages");
            builder.HasKey(message => new { message.MessageId, message.ConsumerName });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ConsumerName).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ProcessedAtUtc).IsRequired();
        });
    }
}
