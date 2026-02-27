using Microsoft.Extensions.Options;
using OsmondLocalApi.Config;
using OsmondLocalApi.Models;

namespace OsmondLocalApi.Services;

public sealed class OsmondReaderService : IOsmondReaderService
{
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly ILogger<OsmondReaderService> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;
    private readonly PrSdkGateway _sdkGateway;
    private volatile bool _initialized;

    public OsmondReaderService(ILogger<OsmondReaderService> logger, IOptionsMonitor<AppSettings> settings)
    {
        _logger = logger;
        _settings = settings;
        _sdkGateway = new PrSdkGateway(logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var cfg = _settings.CurrentValue;
        _logger.LogInformation("Opening Osmond reader device {DeviceName}", cfg.DeviceName);

        var openResult = await _sdkGateway.OpenDeviceAsync(cfg.DeviceName, cancellationToken);
        if (!openResult)
        {
            _logger.LogError("Device initialization failed for {DeviceName}", cfg.DeviceName);
            return;
        }

        _initialized = true;
        _logger.LogInformation("Reader initialized and ready.");
    }

    public async Task<ReadResponse> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _readLock.WaitAsync(0, cancellationToken))
        {
            return Error(ErrorCode.ReadInProgress, "Another read operation is already running.");
        }

        try
        {
            if (!_initialized)
            {
                await InitializeAsync(cancellationToken);
                if (!_initialized)
                {
                    return Error(ErrorCode.DeviceOpenFailed, "Reader is not initialized.");
                }
            }

            var cfg = _settings.CurrentValue;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));

            _logger.LogInformation("Read started.");

            var read = await _sdkGateway.ReadDocumentAsync(includePhoto: cfg.IncludePhoto, timeoutCts.Token);
            if (read.Status == ErrorCode.None)
            {
                _logger.LogInformation("Read finished successfully.");
                return new ReadResponse
                {
                    Ok = true,
                    InternalCode = ErrorCode.None,
                    Message = "Read completed.",
                    Fields = read.Fields,
                    Raw = read.Raw,
                    Images = read.Images
                };
            }

            _logger.LogWarning("Read failed with code {Code}: {Message}", read.Status, read.Message);
            return Error(read.Status, read.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Read operation timed out.");
            return Error(ErrorCode.Timeout, "Read operation timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during read.");
            return Error(ErrorCode.ReadFailed, "Unhandled read error.");
        }
        finally
        {
            _readLock.Release();
        }
    }

    private static ReadResponse Error(ErrorCode code, string message) => new()
    {
        Ok = false,
        InternalCode = code,
        Message = message
    };

    private sealed class PrSdkGateway(ILogger logger)
    {
        private bool _deviceOpen;

        public Task<bool> OpenDeviceAsync(string deviceName, CancellationToken cancellationToken)
        {
            // NOTE:
            // Replace this placeholder with direct pr-sdk-2.2 initialization/open calls.
            // Keep the device open for process lifetime.
            // Example flow:
            // 1) enumerate readers, match by deviceName
            // 2) open reader
            // 3) initialize engine/session
            _deviceOpen = !string.IsNullOrWhiteSpace(deviceName);
            return Task.FromResult(_deviceOpen);
        }

        public async Task<GatewayReadResult> ReadDocumentAsync(bool includePhoto, CancellationToken cancellationToken)
        {
            if (!_deviceOpen)
            {
                return GatewayReadResult.Fail(ErrorCode.DeviceNotFound, "Device is not open.");
            }

            var tcs = new TaskCompletionSource<GatewayReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            // SDK integration contract (pr-sdk-2.2) expected in production:
            // - Subscribe AuthBegin, AuthWaitForInput, AuthFinished
            // - Trigger scan white + infra, then analyze MRZ + VIZ
            // - Always attempt chip authentication and DG read
            // - Wait for ReadFinished(FileId.All) and then resolve TCS
            //
            // Pseudocode:
            // session.AuthBegin += ...
            // session.AuthWaitForInput += ...
            // session.AuthFinished += result => if !result.Success -> tcs.TrySetResult(Fail(ReadFailed, ...));
            // session.ReadFinished += args => if args.FileId == FileId.All -> tcs.TrySetResult(Map(args));
            // session.StartRead(white:true, infra:true, readChip:true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750, cancellationToken); // Placeholder while waiting SDK callbacks.

                    var success = new GatewayReadResult
                    {
                        Status = ErrorCode.None,
                        Message = "Read completed.",
                        Fields = new ReadFields
                        {
                            FullNameAr = "",
                            FullNameLat = "",
                            Dob = "",
                            Sex = "",
                            DocNo = "",
                            Nin = "",
                            Address = "",
                            IssueDate = "",
                            ExpiryDate = ""
                        },
                        Raw = new RawPayload
                        {
                            Mrz = "",
                            Barcode = ""
                        },
                        Images = new ImagePayload
                        {
                            PhotoBase64 = includePhoto ? "" : string.Empty,
                            PhotoMime = "image/jpeg"
                        }
                    };

                    // Image priority in final mapper: DG2 chip face first, fallback to VIZ face.
                    tcs.TrySetResult(success);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(GatewayReadResult.Fail(ErrorCode.Timeout, "Read timed out."));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SDK read failed.");
                    tcs.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, "SDK read failed."));
                }
            }, cancellationToken);

            return await tcs.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class GatewayReadResult
    {
        public ErrorCode Status { get; init; }
        public string Message { get; init; } = string.Empty;
        public ReadFields Fields { get; init; } = new();
        public RawPayload Raw { get; init; } = new();
        public ImagePayload Images { get; init; } = new();

        public static GatewayReadResult Fail(ErrorCode code, string message) => new()
        {
            Status = code,
            Message = message
        };
    }
}
