using BuildingBlocks.SharedKernel;
using FluentValidation;

namespace OrderService.Features.Orders;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public const decimal MaximumOrderTotal = 10_000m;

    public CreateOrderRequestValidator()
    {
        RuleFor(request => request.CustomerId)
            .NotEmpty()
            .Must(id => Guid.TryParse(id, out var parsed) && parsed != Guid.Empty)
            .WithMessage("CustomerId must be a non-empty GUID.");

        RuleFor(request => request.ClientRequestId)
            .NotEmpty()
            .MaximumLength(128);

        RuleFor(request => request.Currency)
            .NotEmpty()
            .Length(3)
            .Must(currency => currency is not null && Money.SupportedCurrencies.Contains(currency))
            .WithMessage(request => $"Currency '{request.Currency}' is not supported.");

        RuleFor(request => request.Items)
            .NotNull()
            .Must(items => items is { Count: > 0 })
            .WithMessage("At least one order item is required.");

        RuleForEach(request => request.Items).ChildRules(item =>
        {
            item.RuleFor(x => x.ProductId)
                .NotEmpty()
                .Must(id => Guid.TryParse(id, out var parsed) && parsed != Guid.Empty)
                .WithMessage("ProductId must be a non-empty GUID.");

            item.RuleFor(x => x.Quantity).GreaterThan(0);
            item.RuleFor(x => x.UnitPrice).GreaterThan(0);
        });

        RuleFor(request => request)
            .Must(NotContainDuplicateProducts)
            .WithMessage("Duplicate product IDs are not allowed in the same order.")
            .When(request => request.Items is { Count: > 0 });

        RuleFor(request => request)
            .Must(HavePositiveTotal)
            .WithMessage("Order total must be greater than zero.")
            .When(request => request.Items is { Count: > 0 });

        RuleFor(request => request)
            .Must(request => CalculateOrderTotal(request) <= MaximumOrderTotal)
            .WithMessage($"Order total cannot exceed {MaximumOrderTotal:0.00}.")
            .When(request => request.Items is { Count: > 0 });
    }

    public static decimal CalculateOrderTotal(CreateOrderRequest request) =>
        request.Items?.Sum(item => item.Quantity * item.UnitPrice) ?? 0m;

    private static bool HavePositiveTotal(CreateOrderRequest request) => CalculateOrderTotal(request) > 0;

    private static bool NotContainDuplicateProducts(CreateOrderRequest request)
    {
        var normalized = request.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductId))
            .Select(item => item.ProductId.Trim().ToUpperInvariant())
            .ToArray();
        return normalized.Length == normalized.Distinct(StringComparer.Ordinal).Count();
    }
}
