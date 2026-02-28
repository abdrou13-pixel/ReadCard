namespace OsmondLocalApi.Services;

public sealed class ReaderHostedService(IOsmondReaderService readerService, ILogger<ReaderHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing reader service at startup.");
        await readerService.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Reader hosted service stopped.");
        return Task.CompletedTask;
    }
}
