namespace InventoryService.Domain;

public sealed class Product
{
    private Product()
    {
    }

    private Product(Guid id, string sku, string name, int availableStock, DateTime utcNow)
    {
        Id = id;
        Sku = sku;
        Name = name;
        AvailableStock = availableStock;
        ReservedStock = 0;
        UpdatedAtUtc = utcNow;
        ConcurrencyToken = Guid.NewGuid();
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public int AvailableStock { get; private set; }
    public int ReservedStock { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public Guid ConcurrencyToken { get; private set; }

    public static Product Create(Guid id, string sku, string name, int availableStock, DateTime utcNow)
    {
        if (availableStock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableStock), "Available stock cannot be negative.");
        }

        return new Product(id, sku, name, availableStock, utcNow);
    }

    public bool CanReserve(int quantity) => quantity > 0 && AvailableStock >= quantity;

    public void Reserve(int quantity, DateTime utcNow)
    {
        if (!CanReserve(quantity))
        {
            throw new InvalidOperationException($"Product '{Id}' does not have enough stock to reserve {quantity} units.");
        }

        AvailableStock -= quantity;
        ReservedStock += quantity;
        UpdatedAtUtc = utcNow;
        ConcurrencyToken = Guid.NewGuid();
    }
    public void Release(int quantity, DateTime utcNow)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Release quantity must be greater than zero.");
        }

        if (ReservedStock < quantity)
        {
            throw new InvalidOperationException($"Product '{Id}' does not have {quantity} reserved units to release.");
        }

        ReservedStock -= quantity;
        AvailableStock += quantity;
        UpdatedAtUtc = utcNow;
        ConcurrencyToken = Guid.NewGuid();
    }

}
