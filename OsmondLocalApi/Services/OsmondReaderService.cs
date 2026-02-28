using Microsoft.Extensions.Options;
using OsmondLocalApi.Config;
using OsmondLocalApi.Models;
using Pr22;
using Pr22.ECardHandling;
using Pr22.Events;
using Pr22.Exceptions;
using Pr22.Imaging;
using Pr22.Processing;
using Pr22.Task;

namespace OsmondLocalApi.Services;

public sealed class OsmondReaderService : IOsmondReaderService, IDisposable
{
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly object _sync = new();
    private readonly ILogger<OsmondReaderService> _logger;
    private readonly IOptionsMonitor<AppSettings> _settings;

    private DocumentReaderDevice? _device;
    private ECard? _card;
    private ECardReader? _reader;
    private TaskControl? _readControl;
    private TaskCompletionSource<GatewayReadResult>? _readFinishedTcs;
    private Processing.Document? _vizResult;
    private volatile bool _initialized;

    public OsmondReaderService(ILogger<OsmondReaderService> logger, IOptionsMonitor<AppSettings> settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await Task.Yield();
        lock (_sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                _device = new DocumentReaderDevice();
                _device.Connection += OnConnection;
                _device.AuthBegin += OnAuthBegin;
                _device.AuthFinished += OnAuthFinished;
                _device.AuthWaitForInput += OnAuthWaitForInput;
                _device.ReadBegin += OnReadBegin;
                _device.ReadFinished += OnReadFinished;
                _device.FileChecked += OnFileChecked;

                OpenConfiguredDevice();
                _initialized = true;
                _logger.LogInformation("Reader SDK initialized and device opened.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SDK initialization failed.");
                _initialized = false;
            }
        }
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
            }

            if (!_initialized || _device is null)
            {
                return Error(ErrorCode.DeviceOpenFailed, "Device initialization failed.");
            }

            var cfg = _settings.CurrentValue;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, cfg.TimeoutSeconds)));

            _logger.LogInformation("Read lifecycle started.");

            var result = await StartReadLifecycleAsync(cfg, timeoutCts.Token);
            if (result.Status != ErrorCode.None)
            {
                return Error(result.Status, result.Message);
            }

            return new ReadResponse
            {
                Ok = true,
                InternalCode = ErrorCode.None,
                Message = "Read completed.",
                Fields = result.Fields,
                Raw = result.Raw,
                Images = result.Images
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Read timed out.");
            return Error(ErrorCode.Timeout, "Read operation timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled read failure.");
            return Error(ErrorCode.ReadFailed, ex.Message);
        }
        finally
        {
            CleanupCardAndControl();
            _readLock.Release();
        }
    }

    private async Task<GatewayReadResult> StartReadLifecycleAsync(AppSettings cfg, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            EnsureDeviceConnected();
            if (_device is null)
            {
                return GatewayReadResult.Fail(ErrorCode.DeviceNotFound, "Device not found.");
            }

            _reader = _device.Readers.FirstOrDefault();
            if (_reader is null)
            {
                return GatewayReadResult.Fail(ErrorCode.NoDocument, "No e-card reader found.");
            }

            var cards = _reader.GetCards();
            if (cards.Count == 0)
            {
                return GatewayReadResult.Fail(ErrorCode.NoDocument, "No document/card present.");
            }

            _card = _reader.ConnectCard(0);
        }

        if (_device is null || _reader is null || _card is null)
        {
            return GatewayReadResult.Fail(ErrorCode.ReadFailed, "Reader/card initialization failed.");
        }

        var scanTask = new DocScannerTask();
        scanTask.Add(Light.Infra).Add(Light.White);
        var page = _device.Scanner.Scan(scanTask, PagePosition.First);

        var engineTask = new EngineTask();
        engineTask.Add(FieldSource.Mrz, FieldId.All);
        engineTask.Add(FieldSource.Viz, FieldId.CAN);
        engineTask.Add(new FieldReference(FieldSource.Viz, FieldId.Face));

        _vizResult = _device.Engine.Analyze(page, engineTask);

        var readTask = new ECardTask
        {
            AuthLevel = AuthLevel.Full
        };
        readTask.Add(FileId.All);

        _readFinishedTcs = new TaskCompletionSource<GatewayReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        _logger.LogInformation("Starting card read with AuthLevel.Full and FileId.All.");
        _readControl = _reader.StartRead(_card, readTask);

        await using var reg = cancellationToken.Register(() => _readFinishedTcs.TrySetCanceled(cancellationToken));
        return await _readFinishedTcs.Task;
    }

    private void OpenConfiguredDevice()
    {
        if (_device is null)
        {
            throw new InvalidOperationException("SDK device is not initialized.");
        }

        var deviceName = _settings.CurrentValue.DeviceName?.Trim();
        var devices = DocumentReaderDevice.GetDeviceList();

        if (devices.Count == 0)
        {
            throw new InvalidOperationException("No connected reader devices detected.");
        }

        var selected = devices.FirstOrDefault(d => string.Equals(d, deviceName, StringComparison.OrdinalIgnoreCase))
                       ?? devices.First();

        _device.UseDevice(selected);
        _logger.LogInformation("Using reader device: {DeviceName}", selected);
    }

    private void EnsureDeviceConnected()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            _ = _device.Readers;
        }
        catch (General)
        {
            _logger.LogWarning("Reader disconnected. Attempting reopen.");
            OpenConfiguredDevice();
        }
    }

    private void OnConnection(object? sender, ConnectionEventArgs e)
    {
        _logger.LogInformation("Connection state changed: {Connection}", e.Connection);
    }

    private void OnAuthBegin(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthBegin: {Auth}", e.Authentication);
    }

    private void OnAuthFinished(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthFinished: {Auth}, result: {Result}", e.Authentication, e.Result);

        if (e.Result != ErrorCodes.ENOERR)
        {
            _readFinishedTcs?.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, $"Chip authentication failed: {e.Result}"));
        }
    }

    private void OnAuthWaitForInput(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthWaitForInput: {Auth}", e.Authentication);

        if (_card is null || _vizResult is null)
        {
            _readFinishedTcs?.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, "Authentication context not ready."));
            return;
        }

        BinData? authData = null;
        var selector = 0;

        if (e.Authentication is AuthProcess.BAC or AuthProcess.BAP or AuthProcess.PACE)
        {
            var mrzRef = new FieldReference(FieldSource.Mrz, FieldId.All);
            var canRef = new FieldReference(FieldSource.Viz, FieldId.CAN);

            if (_vizResult.GetFields(mrzRef).Count > 0)
            {
                authData = new BinData();
                authData.SetString(_vizResult.GetField(mrzRef).GetBestStringValue());
                selector = 1;
            }
            else if (_vizResult.GetFields(canRef).Count > 0)
            {
                authData = new BinData();
                authData.SetString(_vizResult.GetField(canRef).GetBestStringValue());
                selector = 2;
            }
        }

        try
        {
            _card.Authenticate(e.Authentication, authData, selector);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authenticate failed for {Auth}", e.Authentication);
            _readFinishedTcs?.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, "Chip authentication failed."));
        }
    }

    private void OnReadBegin(object? sender, FileEventArgs e)
    {
        _logger.LogInformation("ReadBegin: {FileId}", e.FileId);
    }

    private void OnReadFinished(object? sender, FileEventArgs e)
    {
        _logger.LogInformation("ReadFinished: {FileId}, result: {Result}", e.FileId, e.Result);

        if (e.Result != ErrorCodes.ENOERR && e.FileId.Id == (int)FileId.All)
        {
            _readFinishedTcs?.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, $"Read failed: {e.Result}"));
            return;
        }

        if (e.FileId.Id != (int)FileId.All || _device is null || _card is null)
        {
            return;
        }

        try
        {
            var payload = BuildResult(_device, _card, _settings.CurrentValue.IncludePhoto);
            _readFinishedTcs?.TrySetResult(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed building read payload.");
            _readFinishedTcs?.TrySetResult(GatewayReadResult.Fail(ErrorCode.ReadFailed, "Failed extracting fields."));
        }
    }

    private void OnFileChecked(object? sender, FileEventArgs e)
    {
        _logger.LogInformation("FileChecked: {FileId}, result: {Result}", e.FileId, e.Result);
    }

    private GatewayReadResult BuildResult(DocumentReaderDevice device, ECard card, bool includePhoto)
    {
        var fileData = card.GetFile(FileId.All);
        var document = device.Engine.Analyze(fileData);

        var fields = new ReadFields
        {
            FullNameAr = GetFieldBest(document, FieldSource.Viz, FieldId.Name),
            FullNameLat = GetFieldBest(document, FieldSource.Mrz, FieldId.Name),
            Dob = GetFieldBest(document, FieldSource.Mrz, FieldId.BirthDate),
            Sex = GetFieldBest(document, FieldSource.Mrz, FieldId.Sex),
            DocNo = GetFieldBest(document, FieldSource.Mrz, FieldId.DocumentNumber),
            Nin = GetFieldBest(document, FieldSource.Viz, FieldId.OptionalData),
            Address = GetFieldBest(document, FieldSource.Viz, FieldId.Address),
            IssueDate = GetFieldBest(document, FieldSource.Viz, FieldId.IssueDate),
            ExpiryDate = GetFieldBest(document, FieldSource.Mrz, FieldId.ExpiryDate)
        };

        var raw = new RawPayload
        {
            Mrz = GetFieldRaw(document, FieldSource.Mrz, FieldId.All),
            Barcode = GetFieldRaw(document, FieldSource.Viz, FieldId.Barcode)
        };

        var images = new ImagePayload();
        if (includePhoto)
        {
            var face = TryGetFaceImageBase64(document) ?? string.Empty;
            images.PhotoBase64 = face;
            images.PhotoMime = "image/jpeg";
        }

        return new GatewayReadResult
        {
            Status = ErrorCode.None,
            Message = "Read completed.",
            Fields = fields,
            Raw = raw,
            Images = images
        };
    }

    private static string? TryGetFaceImageBase64(Processing.Document doc)
    {
        // Priority: DG2 face, fallback: VIZ face
        var dg2Ref = new FieldReference(FieldSource.ECard, FieldId.Face);
        if (doc.GetFields().Contains(dg2Ref))
        {
            return Convert.ToBase64String(doc.GetField(dg2Ref).GetImage().Save(Image.FileFormat.Jpeg).GetBytes());
        }

        var vizRef = new FieldReference(FieldSource.Viz, FieldId.Face);
        if (doc.GetFields().Contains(vizRef))
        {
            return Convert.ToBase64String(doc.GetField(vizRef).GetImage().Save(Image.FileFormat.Jpeg).GetBytes());
        }

        return null;
    }

    private static string GetFieldBest(Processing.Document doc, FieldSource source, FieldId id)
    {
        try
        {
            return doc.GetField(source, id).GetBestStringValue();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetFieldRaw(Processing.Document doc, FieldSource source, FieldId id)
    {
        try
        {
            return doc.GetField(source, id).GetRawStringValue();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void CleanupCardAndControl()
    {
        try
        {
            _readControl?.Stop().Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore stop cleanup errors.
        }

        _readControl = null;

        try
        {
            _card?.Disconnect();
        }
        catch
        {
            // Ignore disconnect cleanup errors.
        }

        _card = null;
        _reader = null;
        _vizResult = null;
        _readFinishedTcs = null;
    }

    private static ReadResponse Error(ErrorCode code, string message) => new()
    {
        Ok = false,
        InternalCode = code,
        Message = message
    };

    public void Dispose()
    {
        CleanupCardAndControl();

        if (_device is not null)
        {
            try
            {
                _device.Close();
            }
            catch
            {
                // ignored
            }
        }

        _readLock.Dispose();
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
