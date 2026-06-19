using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaymentService.Persistence;

namespace PaymentService.Features.Payments;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController : ControllerBase
{
    private readonly PaymentDbContext _dbContext;

    public PaymentsController(PaymentDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPayments(CancellationToken cancellationToken)
    {
        var payments = await _dbContext.PaymentAttempts
            .AsNoTracking()
            .OrderByDescending(payment => payment.CreatedAtUtc)
            .Select(payment => new PaymentResponse(
                payment.Id,
                payment.OrderId,
                payment.Amount,
                payment.Currency,
                payment.Status.ToString(),
                payment.ProviderTransactionId,
                payment.FailureCode,
                payment.FailureReason,
                payment.CreatedAtUtc,
                payment.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);

        return Ok(payments);
    }

    [HttpGet("orders/{orderId:guid}")]
    [ProducesResponseType(typeof(PaymentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPaymentForOrder(Guid orderId, CancellationToken cancellationToken)
    {
        var payment = await _dbContext.PaymentAttempts
            .AsNoTracking()
            .Where(payment => payment.OrderId == orderId)
            .Select(payment => new PaymentResponse(
                payment.Id,
                payment.OrderId,
                payment.Amount,
                payment.Currency,
                payment.Status.ToString(),
                payment.ProviderTransactionId,
                payment.FailureCode,
                payment.FailureReason,
                payment.CreatedAtUtc,
                payment.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return payment is null ? NotFound() : Ok(payment);
    }

    [HttpGet("cancellations")]
    [ProducesResponseType(typeof(IReadOnlyCollection<PaymentCancellationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCancellations(CancellationToken cancellationToken)
    {
        var cancellations = await _dbContext.CancelledOrders
            .AsNoTracking()
            .OrderByDescending(cancellation => cancellation.CreatedAtUtc)
            .Select(cancellation => new PaymentCancellationResponse(
                cancellation.OrderId,
                cancellation.CustomerId,
                cancellation.Reason,
                cancellation.Status.ToString(),
                cancellation.CancelledAtUtc,
                cancellation.CreatedAtUtc,
                cancellation.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);

        return Ok(cancellations);
    }

    [HttpGet("orders/{orderId:guid}/cancellation")]
    [ProducesResponseType(typeof(PaymentCancellationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCancellationForOrder(Guid orderId, CancellationToken cancellationToken)
    {
        var cancellation = await _dbContext.CancelledOrders
            .AsNoTracking()
            .Where(cancellation => cancellation.OrderId == orderId)
            .Select(cancellation => new PaymentCancellationResponse(
                cancellation.OrderId,
                cancellation.CustomerId,
                cancellation.Reason,
                cancellation.Status.ToString(),
                cancellation.CancelledAtUtc,
                cancellation.CreatedAtUtc,
                cancellation.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return cancellation is null ? NotFound() : Ok(cancellation);
    }
}
