using Xunit;
using FluentAssertions;
using OrderService.Features.Orders;

namespace OrderService.UnitTests;

public sealed class CreateOrderRequestValidatorTests
{
    private readonly CreateOrderRequestValidator _validator = new();

    [Fact]
    public void Should_accept_order_when_request_is_valid()
    {
        var request = ValidRequest();

        var result = _validator.Validate(request);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Should_reject_order_when_no_items_are_provided()
    {
        var request = ValidRequest(items: []);

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("At least one order item"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Should_reject_order_when_quantity_is_not_positive(int quantity)
    {
        var request = ValidRequest(items: [new CreateOrderItemRequest(Guid.NewGuid().ToString(), quantity, 12.34m)]);

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName.EndsWith("Quantity"));
    }

    [Fact]
    public void Should_reject_order_when_customer_id_is_missing()
    {
        var request = ValidRequest(customerId: string.Empty);

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(CreateOrderRequest.CustomerId));
    }

    [Fact]
    public void Should_reject_order_when_customer_id_format_is_invalid()
    {
        var request = ValidRequest(customerId: "not-a-guid");

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("GUID"));
    }

    [Fact]
    public void Should_reject_duplicate_product_ids()
    {
        var productId = Guid.NewGuid().ToString();
        var request = ValidRequest(items:
        [
            new CreateOrderItemRequest(productId, 1, 10m),
            new CreateOrderItemRequest(productId, 2, 10m)
        ]);

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("Duplicate product IDs"));
    }

    [Fact]
    public void Should_calculate_total_correctly()
    {
        var request = ValidRequest(items:
        [
            new CreateOrderItemRequest(Guid.NewGuid().ToString(), 2, 10.15m),
            new CreateOrderItemRequest(Guid.NewGuid().ToString(), 3, 2.50m)
        ]);

        var total = CreateOrderRequestValidator.CalculateOrderTotal(request);

        total.Should().Be(27.80m);
    }

    [Fact]
    public void Should_reject_order_when_total_exceeds_maximum_limit()
    {
        var request = ValidRequest(items: [new CreateOrderItemRequest(Guid.NewGuid().ToString(), 2, 5_001m)]);

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("cannot exceed"));
    }

    [Fact]
    public void Should_reject_unsupported_currency()
    {
        var request = ValidRequest(currency: "BTC");

        var result = _validator.Validate(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.ErrorMessage.Contains("not supported"));
    }

    private static CreateOrderRequest ValidRequest(
        string? customerId = null,
        string currency = "USD",
        IReadOnlyCollection<CreateOrderItemRequest>? items = null)
    {
        return new CreateOrderRequest(
            customerId ?? Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            currency,
            items ?? [new CreateOrderItemRequest(Guid.NewGuid().ToString(), 2, 12.34m)]);
    }
}
