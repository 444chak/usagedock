using UsageDock.Models;

namespace UsageDock.Providers;

public interface IUsageProvider
{
    ProviderId Id { get; }

    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken);
}
