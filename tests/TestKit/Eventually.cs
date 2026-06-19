namespace TestKit;

public static class Eventually
{
    public static async Task ShouldPass(
        Func<Task> assertion,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var stopAt = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var delay = interval ?? TimeSpan.FromMilliseconds(250);
        Exception? lastException = null;

        while (DateTime.UtcNow < stopAt)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(delay);
            }
        }

        throw new TimeoutException("Assertion did not pass within timeout.", lastException);
    }
}
