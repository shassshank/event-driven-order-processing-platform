using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain;

namespace OrderService.Persistence;

public sealed class OrderDbContext : DbContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
    {
    }

    public DbSet<OrderAggregate> Orders => Set<OrderAggregate>();
    public DbSet<OrderItemEntity> OrderItems => Set<OrderItemEntity>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderAggregate>(builder =>
        {
            builder.ToTable("orders");
            builder.HasKey(order => order.Id);
            builder.Property(order => order.CustomerId).IsRequired();
            builder.Property(order => order.ClientRequestId).HasMaxLength(128).IsRequired();
            builder.HasIndex(order => order.ClientRequestId).IsUnique();
            builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
            builder.Property(order => order.TotalAmount).HasPrecision(18, 2).IsRequired();
            builder.Property(order => order.Currency).HasMaxLength(3).IsRequired();
            builder.Property(order => order.CreatedAtUtc).IsRequired();
            builder.Property(order => order.UpdatedAtUtc).IsRequired();
            builder.HasMany(order => order.Items)
                .WithOne()
                .HasForeignKey(item => item.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Metadata.FindNavigation(nameof(OrderAggregate.Items))?.SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OrderItemEntity>(builder =>
        {
            builder.ToTable("order_items");
            builder.HasKey(item => item.Id);
            builder.Property(item => item.ProductId).IsRequired();
            builder.Property(item => item.Quantity).IsRequired();
            builder.Property(item => item.UnitPrice).HasPrecision(18, 2).IsRequired();
            builder.Property(item => item.LineTotal).HasPrecision(18, 2).IsRequired();
            builder.HasIndex(item => new { item.OrderId, item.ProductId }).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.ToTable("outbox_messages");
            builder.HasKey(message => message.Id);
            builder.HasIndex(message => message.MessageId).IsUnique();
            builder.HasIndex(message => new { message.Status, message.OccurredOnUtc });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.RoutingKey).HasMaxLength(256).IsRequired();
            builder.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            builder.Property(message => message.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            builder.Property(message => message.Error).HasMaxLength(4000);
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.ToTable("processed_messages");
            builder.HasKey(message => new { message.MessageId, message.ConsumerName });
            builder.Property(message => message.EventType).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ConsumerName).HasMaxLength(256).IsRequired();
            builder.Property(message => message.ProcessedAtUtc).IsRequired();
        });
    }
}
