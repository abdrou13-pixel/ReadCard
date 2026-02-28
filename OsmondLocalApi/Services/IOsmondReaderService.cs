using OsmondLocalApi.Models;

namespace OsmondLocalApi.Services;

public interface IOsmondReaderService
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<ReadResponse> ReadAsync(CancellationToken cancellationToken);
}
