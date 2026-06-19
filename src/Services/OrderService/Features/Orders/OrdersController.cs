using BuildingBlocks.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace OrderService.Features.Orders;

[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IOrderApplicationService _orders;

    public OrdersController(IOrderApplicationService orders) => _orders = orders;

    [HttpPost]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await _orders.CreateOrderAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return ToProblem(result.Error);
        }

        return CreatedAtAction(nameof(GetOrderById), new { id = result.Value!.Id }, result.Value);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken)
    {
        var result = await _orders.GetOrderByIdAsync(id, cancellationToken);
        return result.IsFailure ? ToProblem(result.Error) : Ok(result.Value);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<OrderResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOrders(CancellationToken cancellationToken)
    {
        var orders = await _orders.GetOrdersAsync(cancellationToken);
        return Ok(orders);
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelOrder(Guid id, [FromBody] CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var result = await _orders.CancelOrderAsync(id, request.Reason, cancellationToken);
        return result.IsFailure ? ToProblem(result.Error) : Ok(result.Value);
    }

    private ObjectResult ToProblem(Error error)
    {
        var statusCode = error.Code switch
        {
            "orders.not_found" => StatusCodes.Status404NotFound,
            "orders.duplicate_client_request_id" => StatusCodes.Status409Conflict,
            "orders.already_completed" => StatusCodes.Status409Conflict,
            "orders.payment_processing" => StatusCodes.Status409Conflict,
            "orders.invalid_transition" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };

        return Problem(
            title: error.Code,
            detail: error.Message,
            statusCode: statusCode,
            type: $"https://errors.order-platform.local/{error.Code}");
    }
}

public sealed record CancelOrderRequest(string Reason);
