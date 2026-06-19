namespace InventoryService.Features.Inventory;

public sealed record ProductResponse(Guid Id, string Sku, string Name, int AvailableStock, int ReservedStock, DateTime UpdatedAtUtc);
