using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NotificationService.Domain;
using NotificationService.Persistence;

namespace NotificationService.Features.Notifications;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationDbContext _dbContext;

    public NotificationsController(NotificationDbContext dbContext) => _dbContext = dbContext;

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyCollection<NotificationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications(CancellationToken cancellationToken)
    {
        var notifications = await _dbContext.Notifications
            .AsNoTracking()
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Take(100)
            .Select(notification => ToResponse(notification))
            .ToArrayAsync(cancellationToken);

        return Ok(notifications);
    }

    [HttpGet("orders/{orderId:guid}")]
    [ProducesResponseType(typeof(IReadOnlyCollection<NotificationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotificationsForOrder(Guid orderId, CancellationToken cancellationToken)
    {
        var notifications = await _dbContext.Notifications
            .AsNoTracking()
            .Where(notification => notification.OrderId == orderId)
            .OrderByDescending(notification => notification.CreatedAtUtc)
            .Select(notification => ToResponse(notification))
            .ToArrayAsync(cancellationToken);

        return Ok(notifications);
    }

    private static NotificationResponse ToResponse(NotificationMessage notification) =>
        new(
            notification.Id,
            notification.MessageId,
            notification.OrderId,
            notification.TriggerEventType,
            notification.Channel,
            notification.Recipient,
            notification.Template,
            notification.Status,
            notification.Error,
            notification.CreatedAtUtc);
}
