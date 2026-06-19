using BuildingBlocks.Persistence;
using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Persistence;

public sealed class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<InventoryReservation> Reservations => Set<InventoryReservation>();
    public DbSet<CancelledOrder> CancelledOrders => Set<CancelledOrder>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(builder =>
        {
            builder.ToTable("inventory_products");
            builder.HasKey(product => product.Id);
            builder.Property(product => product.Sku).HasMaxLength(64).IsRequired();
            builder.Property(product => product.Name).HasMaxLength(256).IsRequired();
            builder.Property(product => product.AvailableStock).IsRequired();
            builder.Property(product => product.ReservedStock).IsRequired();
            builder.Property(product => product.UpdatedAtUtc).IsRequired();
            builder.Property(product => product.ConcurrencyToken).IsConcurrencyToken().IsRequired();
            builder.HasIndex(product => product.Sku).IsUnique();
        });

        modelBuilder.Entity<InventoryReservation>(builder =>
        {
            builder.ToTable("inventory_reservations");
            builder.HasKey(reservation => new { reservation.OrderId, reservation.ProductId });
            builder.Property(reservation => reservation.Quantity).IsRequired();
            builder.Property(reservation => reservation.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(reservation => reservation.CreatedAtUtc).IsRequired();
        });


        modelBuilder.Entity<CancelledOrder>(builder =>
        {
            builder.ToTable("inventory_cancelled_orders");
            builder.HasKey(cancelled => cancelled.OrderId);
            builder.HasIndex(cancelled => cancelled.MessageId).IsUnique();
            builder.Property(cancelled => cancelled.CustomerId).IsRequired();
            builder.Property(cancelled => cancelled.Reason).HasMaxLength(512).IsRequired();
            builder.Property(cancelled => cancelled.MessageId).IsRequired();
            builder.Property(cancelled => cancelled.CancelledAtUtc).IsRequired();
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.ToTable("inventory_processed_messages");
            builder.HasKey(message => new { message.MessageId, message.ConsumerName });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ConsumerName).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("inventory_outbox_messages");
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
