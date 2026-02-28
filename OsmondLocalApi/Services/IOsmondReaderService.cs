using OsmondLocalApi.Models;

namespace OsmondLocalApi.Services;

public interface IOsmondReaderService
{
    Task<ReadResponse> ReadAsync(CancellationToken cancellationToken);
}
