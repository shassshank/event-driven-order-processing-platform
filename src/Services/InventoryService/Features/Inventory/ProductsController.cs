using InventoryService.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Features.Inventory;

[ApiController]
[Route("api/products")]
public sealed class ProductsController : ControllerBase
{
    private readonly InventoryDbContext _dbContext;

    public ProductsController(InventoryDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProducts(CancellationToken cancellationToken)
    {
        var products = await _dbContext.Products
            .AsNoTracking()
            .OrderBy(product => product.Sku)
            .Select(product => new ProductResponse(
                product.Id,
                product.Sku,
                product.Name,
                product.AvailableStock,
                product.ReservedStock,
                product.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);

        return Ok(products);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken cancellationToken)
    {
        var product = await _dbContext.Products
            .AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new ProductResponse(
                product.Id,
                product.Sku,
                product.Name,
                product.AvailableStock,
                product.ReservedStock,
                product.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return product is null ? NotFound() : Ok(product);
    }
}
