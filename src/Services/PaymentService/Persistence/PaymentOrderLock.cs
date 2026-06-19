using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace PaymentService.Persistence;

public static class PaymentOrderLock
{
    public static Task AcquireAsync(PaymentDbContext dbContext, Guid orderId, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        var lockKey = ToAdvisoryLockKey(orderId);
        return dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"select pg_advisory_xact_lock({lockKey})",
            cancellationToken);
    }

    public static long ToAdvisoryLockKey(Guid orderId)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!orderId.TryWriteBytes(bytes))
        {
            throw new InvalidOperationException("Could not convert order id to bytes for advisory locking.");
        }

        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(bytes, hash, out var bytesWritten) || bytesWritten < 8)
        {
            throw new InvalidOperationException("Could not hash order id for advisory locking.");
        }

        var lockKey = BinaryPrimitives.ReadInt64LittleEndian(hash[..8]);
        return lockKey == 0 ? 1 : lockKey;
    }
}
