using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReportingService.Domain;
using ReportingService.Persistence;

namespace ReportingService.Features.Reports;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly ReportingDbContext _dbContext;

    public ReportsController(ReportingDbContext dbContext) => _dbContext = dbContext;

    [HttpGet("orders")]
    [ProducesResponseType(typeof(IReadOnlyCollection<ReportingOrderResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrders(CancellationToken cancellationToken)
    {
        var orders = await _dbContext.Orders
            .AsNoTracking()
            .OrderByDescending(order => order.UpdatedAtUtc)
            .Take(100)
            .Select(order => ToResponse(order))
            .ToArrayAsync(cancellationToken);

        return Ok(orders);
    }

    [HttpGet("orders/{orderId:guid}")]
    [ProducesResponseType(typeof(ReportingOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(order => order.OrderId == orderId, cancellationToken);
        return order is null ? NotFound() : Ok(ToResponse(order));
    }

    [HttpGet("events")]
    [ProducesResponseType(typeof(IReadOnlyCollection<ReportingEventResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(CancellationToken cancellationToken)
    {
        var events = await _dbContext.Events
            .AsNoTracking()
            .OrderByDescending(e => e.RecordedAtUtc)
            .Take(200)
            .Select(e => ToResponse(e))
            .ToArrayAsync(cancellationToken);

        return Ok(events);
    }

    [HttpGet("orders/{orderId:guid}/events")]
    [ProducesResponseType(typeof(IReadOnlyCollection<ReportingEventResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrderEvents(Guid orderId, CancellationToken cancellationToken)
    {
        var events = await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.OrderId == orderId)
            .OrderBy(e => e.OccurredOnUtc)
            .Select(e => ToResponse(e))
            .ToArrayAsync(cancellationToken);

        return Ok(events);
    }

    private static ReportingOrderResponse ToResponse(ReportingOrder order) =>
        new(
            order.OrderId,
            order.CustomerId,
            order.Status,
            order.TotalAmount,
            order.Currency,
            order.LastEventType,
            order.CreatedAtUtc,
            order.UpdatedAtUtc);

    private static ReportingEventResponse ToResponse(ReportingEvent e) =>
        new(e.Id, e.MessageId, e.CorrelationId, e.OrderId, e.EventType, e.Source, e.OccurredOnUtc, e.RecordedAtUtc);
}
