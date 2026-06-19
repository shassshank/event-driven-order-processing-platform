using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;

namespace NotificationService.Persistence;

public sealed class NotificationDbContext : DbContext
{
    public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options)
    {
    }

    public DbSet<NotificationMessage> Notifications => Set<NotificationMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationMessage>(builder =>
        {
            builder.ToTable("notification_messages");
            builder.HasKey(message => message.Id);
            builder.HasIndex(message => message.MessageId).IsUnique();
            builder.HasIndex(message => message.OrderId);
            builder.Property(message => message.TriggerEventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.Channel).HasMaxLength(64).IsRequired();
            builder.Property(message => message.Recipient).HasMaxLength(256).IsRequired();
            builder.Property(message => message.Template).HasMaxLength(128).IsRequired();
            builder.Property(message => message.Status).HasMaxLength(32).IsRequired();
            builder.Property(message => message.Error).HasMaxLength(4000);
            builder.Property(message => message.CreatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.ToTable("notification_processed_messages");
            builder.HasKey(message => new { message.MessageId, message.ConsumerName });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ConsumerName).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("notification_outbox_messages");
            builder.HasKey(message => message.Id);
            builder.HasIndex(message => message.MessageId).IsUnique();
            builder.HasIndex(message => new { message.Status, message.OccurredOnUtc });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.RoutingKey).HasMaxLength(256).IsRequired();
            builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(message => message.Error).HasMaxLength(4000);
        });
    }
}
