using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;

namespace PaymentService.Persistence;

public sealed class PaymentDbContext : DbContext
{
    public PaymentDbContext(DbContextOptions<PaymentDbContext> options) : base(options)
    {
    }

    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentCancelledOrder> CancelledOrders => Set<PaymentCancelledOrder>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentAttempt>(builder =>
        {
            builder.ToTable("payment_attempts");
            builder.HasKey(payment => payment.Id);
            builder.HasIndex(payment => payment.OrderId).IsUnique();
            builder.Property(payment => payment.Amount).HasPrecision(18, 2).IsRequired();
            builder.Property(payment => payment.Currency).HasMaxLength(3).IsRequired();
            builder.Property(payment => payment.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(payment => payment.ProviderTransactionId).HasMaxLength(128);
            builder.HasIndex(payment => payment.ProviderTransactionId).IsUnique();
            builder.Property(payment => payment.FailureCode).HasMaxLength(64);
            builder.Property(payment => payment.FailureReason).HasMaxLength(512);
            builder.Property(payment => payment.CreatedAtUtc).IsRequired();
            builder.Property(payment => payment.UpdatedAtUtc).IsRequired();
        });



        modelBuilder.Entity<PaymentCancelledOrder>(builder =>
        {
            builder.ToTable("payment_cancelled_orders");
            builder.HasKey(cancellation => cancellation.OrderId);
            builder.Property(cancellation => cancellation.CustomerId).IsRequired();
            builder.Property(cancellation => cancellation.Reason).HasMaxLength(512).IsRequired();
            builder.Property(cancellation => cancellation.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(cancellation => cancellation.CancelledAtUtc).IsRequired();
            builder.Property(cancellation => cancellation.CreatedAtUtc).IsRequired();
            builder.Property(cancellation => cancellation.UpdatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.ToTable("payment_processed_messages");
            builder.HasKey(message => new { message.MessageId, message.ConsumerName });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ConsumerName).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("payment_outbox_messages");
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
