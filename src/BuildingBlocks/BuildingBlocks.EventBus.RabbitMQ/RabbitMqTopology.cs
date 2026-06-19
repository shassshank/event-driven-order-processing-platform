namespace BuildingBlocks.EventBus.RabbitMQ;

public static class RabbitMqTopology
{
    public const string Exchange = "order-platform.exchange";
    public const string DeadLetterExchange = "order-platform.dlx";

    public static class Queues
    {
        public const string InventoryOrderCreated = "inventory.order-created.queue";
        public const string PaymentInventoryReserved = "payment.inventory-reserved.queue";
        public const string PaymentOrderCancelled = "payment.order-cancelled.queue";
        public const string OrderInventoryEvents = "order.inventory-events.queue";
        public const string OrderPaymentEvents = "order.payment-events.queue";
        public const string InventoryPaymentFailed = "inventory.payment-failed.queue";
        public const string InventoryOrderCancelled = "inventory.order-cancelled.queue";
        public const string NotificationOrderEvents = "notification.order-events.queue";
        public const string ReportingAllEvents = "reporting.all-events.queue";
    }

    public static class RetryExchanges
    {
        public const string Retry5Seconds = "order-platform.retry.5s.exchange";
        public const string Retry30Seconds = "order-platform.retry.30s.exchange";
        public const string Retry2Minutes = "order-platform.retry.2m.exchange";
    }

    public static class RetryQueues
    {
        public const string Retry5Seconds = "retry.5s.queue";
        public const string Retry30Seconds = "retry.30s.queue";
        public const string Retry2Minutes = "retry.2m.queue";
    }

    public static class DeadLetterQueues
    {
        public const string InventoryFailed = "inventory.failed.dlq";
        public const string PaymentFailed = "payment.failed.dlq";
        public const string NotificationFailed = "notification.failed.dlq";
        public const string ReportingFailed = "reporting.failed.dlq";
    }
}
