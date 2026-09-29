using MoynaPay.Application.Abstractions;

namespace MoynaPay.Application.AppHome;

public sealed class AppHomeService(IOrderStore orders, IClock clock)
{
    public Task<HomeMetrics> NumbersAsync(Guid merchantId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var sevenDayStart = todayStart.AddDays(-6);

        return orders.HomeMetricsAsync(merchantId, todayStart, sevenDayStart, now, ct);
    }
}
