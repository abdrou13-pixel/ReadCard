using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using OsmondLocalApi.Models;
using Pr22;
using Pr22.ECardHandling;
using Pr22.Events;
using Pr22.Exceptions;
using Pr22.Imaging;
using Pr22.Processing;
using Pr22.Task;

namespace OsmondLocalApi.Services;

public sealed class OsmondReaderService : IOsmondReaderService, IHostedService, IDisposable
{
    private readonly ILogger<OsmondReaderService> _logger;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly SemaphoreSlim _readSemaphore = new(1, 1);
    private readonly object _stateLock = new();

    private DocumentReaderDevice? _pr;
    private bool _deviceReady;

    private ECard? _currentCard;
    private TaskControl? _currentTaskCtrl;
    private TaskCompletionSource<bool>? _currentReadTcs;

    private string? _mrzString;
    private string? _canString;

    private readonly ConcurrentDictionary<string, Pr22.Processing.Document> _chipDocs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requestedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _receivedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _resultLock = new();
    private ReadResponse _workingResponse = new();
    private volatile bool _authFailed;
    private volatile bool _started;

    public OsmondReaderService(ILogger<OsmondReaderService> logger, IOptionsMonitor<AppConfig> config)
    {
        _logger = logger;
        _config = config;
    }


    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        return StartAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _pr = new DocumentReaderDevice();
            _pr.Connection += OnConnection;
            _pr.AuthBegin += OnAuthBegin;
            _pr.AuthFinished += OnAuthFinished;
            _pr.AuthWaitForInput += OnAuthWaitForInput;
            _pr.ReadBegin += OnReadBegin;
            _pr.ReadFinished += OnReadFinished;
            _pr.FileChecked += OnFileChecked;

            TryOpenConfiguredDevice();
            _started = true;
        }
        catch (DllNotFoundException ex)
        {
            _deviceReady = false;
            _started = false;
            _logger.LogError(ex, "Pr22 SDK is not installed correctly or has wrong bitness.");
        }
        catch (Exception ex)
        {
            _deviceReady = false;
            _started = false;
            _logger.LogError(ex, "Unable to initialize reader service.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _currentTaskCtrl?.Stop();
        }

        lock (_stateLock)
        {
            if (_currentCard is not null)
            {
                try
                {
                    _currentCard.Disconnect();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Card disconnect failed during stop.");
                }

                _currentCard.Dispose();
                _currentCard = null;
            }

            _currentTaskCtrl = null;
            _currentReadTcs = null;
        }

        if (_pr is not null)
        {
            try
            {
                _pr.Close();
                _logger.LogInformation("Reader device closed.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while closing reader device.");
            }
        }

        _started = false;
        return Task.CompletedTask;
    }

    public async Task<ReadResponse> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await _readSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ReadResponse.Failure(ResponseCode.ReadInProgress, "A read operation is already running.");
        }

        try
        {
            if (!_deviceReady || _pr is null)
            {
                return ReadResponse.Failure(ResponseCode.DeviceNotFound, "Reader device is not ready.");
            }

            ResetWorkingState();

            var config = _config.CurrentValue;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));

            var scanTask = new DocScannerTask();
            scanTask.Add(Light.Infra).Add(Light.White);
            var page = _pr.Scanner.Scan(scanTask, PagePosition.First);

            var engTask = new EngineTask();
            engTask.Add(FieldSource.Mrz, FieldId.All)
                .Add(FieldSource.Viz, FieldId.Face)
                .Add(FieldSource.Viz, FieldId.CAN)
                .Add(FieldSource.Viz, FieldId.Signature);

            var vizDoc = _pr.Engine.Analyze(page, engTask);
            _workingResponse.Raw.Mrz = SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.All);
            _workingResponse.Raw.Barcode = SafeFieldValue(vizDoc, FieldSource.Barcode, FieldId.All);
            _mrzString = _workingResponse.Raw.Mrz;
            _canString = SafeFieldValue(vizDoc, FieldSource.Viz, FieldId.CAN);

            if (config.IncludePhoto)
            {
                _workingResponse.Images.PhotoBase64 = TryImageAsJpegBase64(vizDoc, FieldSource.Viz, FieldId.Face);
            }

            var reader = TryConnectCard(out var card);
            if (reader is null || card is null)
            {
                MergeFinalFields(vizDoc);
                var hasVisualData = HasVisualData(_workingResponse);
                if (hasVisualData)
                {
                    _workingResponse.Ok = true;
                    _workingResponse.InternalCode = ResponseCode.Ok;
                    _workingResponse.Message = "Visual read completed; no chip document detected.";
                    return _workingResponse;
                }

                return ReadResponse.Failure(ResponseCode.NoDocument, "No document detected on any reader.");
            }

            lock (_stateLock)
            {
                _currentCard = card;
                _currentReadTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var ecTask = BuildECardTask(config.GetAuthLevelOrDefault());
            _currentTaskCtrl = reader.StartRead(card, ecTask);

            try
            {
                await _currentReadTcs!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _currentTaskCtrl?.Stop();
                return ReadResponse.Failure(ResponseCode.Timeout, "Read operation timed out.");
            }

            MergeFinalFields(vizDoc);

            _workingResponse.Ok = true;
            _workingResponse.InternalCode = ResponseCode.Ok;
            _workingResponse.Message = _authFailed
                ? "MRZ/VIZ read succeeded; chip authentication failed. Returning partial data."
                : _receivedFiles.Count == 0
                    ? "MRZ/VIZ read succeeded; chip data unavailable."
                    : "Read completed successfully.";

            return _workingResponse;
        }
        catch (NoSuchDevice ex)
        {
            _logger.LogError(ex, "No configured device found.");
            return ReadResponse.Failure(ResponseCode.DeviceNotFound, "Configured device not found.");
        }
        catch (DeviceIsDisconnected ex)
        {
            _logger.LogError(ex, "Device disconnected.");
            _deviceReady = false;
            return ReadResponse.Failure(ResponseCode.DeviceNotFound, "Device disconnected.");
        }
        catch (CommunicationError ex)
        {
            _logger.LogError(ex, "Communication error while reading chip.");
            return ReadResponse.Failure(ResponseCode.ChipReadFailed, "Chip communication failed.");
        }
        catch (FunctionTimedOut ex)
        {
            _logger.LogError(ex, "SDK function timed out.");
            return ReadResponse.Failure(ResponseCode.Timeout, "Reader operation timed out.");
        }
        catch (AuthenticityFailed ex)
        {
            _logger.LogError(ex, "Authenticity check failed.");
            return ReadResponse.Failure(ResponseCode.AuthFailed, "Chip authentication failed.");
        }
        catch (InvalidParameter ex)
        {
            _logger.LogError(ex, "Invalid parameter passed to SDK.");
            return ReadResponse.Failure(ResponseCode.ReadFailed, "Reader parameter error.");
        }
        catch (General ex)
        {
            _logger.LogError(ex, "Pr22 read failure.");
            return ReadResponse.Failure(ResponseCode.ReadFailed, "Reader operation failed.");
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "Pr22 runtime missing.");
            return ReadResponse.Failure(ResponseCode.DeviceOpenFailed, "Passport Reader Software not installed or wrong bitness (x64 required).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled read failure.");
            return ReadResponse.Failure(ResponseCode.ReadFailed, "Unexpected read failure.");
        }
        finally
        {
            await CleanupCurrentReadAsync().ConfigureAwait(false);
            _readSemaphore.Release();
        }
    }

    private void TryOpenConfiguredDevice()
    {
        if (_pr is null)
        {
            return;
        }

        var configuredDevice = _config.CurrentValue.DeviceName?.Trim();
        var devices = DocumentReaderDevice.GetDeviceList();

        if (string.IsNullOrWhiteSpace(configuredDevice) || !devices.Contains(configuredDevice, StringComparer.Ordinal))
        {
            _deviceReady = false;
            _logger.LogError("Configured device '{DeviceName}' not found in available list.", configuredDevice);
            return;
        }

        _pr.UseDevice(configuredDevice);
        _deviceReady = true;
        _logger.LogInformation("Reader opened: {DeviceName}", configuredDevice);
    }

    private ECardReader? TryConnectCard(out ECard? card)
    {
        card = null;
        if (_pr is null)
        {
            return null;
        }

        foreach (var reader in _pr.Readers)
        {
            try
            {
                var cards = reader.GetCards();
                if (cards.Count == 0)
                {
                    continue;
                }

                card = reader.ConnectCard(0);
                return reader;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ConnectCard failed on reader {ReaderType}", reader.Info.HwType);
            }
        }

        return null;
    }

    private static ECardTask BuildECardTask(AuthLevel level)
    {
        var ecTask = new ECardTask
        {
            AuthLevel = level
        };

        ecTask.Add(FileId.All)
            .Add(FileId.PersonalDetails)
            .Add(FileId.GeneralData)
            .Add(FileId.DomesticData)
            .Add(FileId.IssuerDetails)
            .Add(FileId.AnyFace)
            .Add(FileId.Signature);

        try
        {
            ecTask.Add(FileId.Sod)
                .Add(FileId.CardAccess)
                .Add(FileId.CardSecurity);
        }
        catch
        {
        }

        return ecTask;
    }

    private Task CleanupCurrentReadAsync()
    {
        ECard? card = null;

        lock (_stateLock)
        {
            _currentTaskCtrl?.Stop();
            card = _currentCard;
            _currentTaskCtrl = null;
            _currentCard = null;
            _currentReadTcs = null;
        }

        if (card is not null)
        {
            try
            {
                card.Disconnect();
            }
            catch
            {
            }

            card.Dispose();
        }

        return Task.CompletedTask;
    }

    private void ResetWorkingState()
    {
        _mrzString = null;
        _canString = null;
        _chipDocs.Clear();
        _authFailed = false;

        lock (_resultLock)
        {
            _requestedFiles.Clear();
            _receivedFiles.Clear();
            _workingResponse = new ReadResponse();
        }
    }

    private void MergeFinalFields(Pr22.Processing.Document vizDoc)
    {
        lock (_resultLock)
        {
            var chipFields = BuildChipAggregateFields();

            _workingResponse.Fields.FullNameLat = FirstNonEmpty(
                chipFields.SurnameGiven,
                BuildLatNameFromViz(vizDoc));

            _workingResponse.Fields.FullNameAr = chipFields.FullNameAr;
            _workingResponse.Fields.Dob = FirstNonEmpty(chipFields.BirthDate, NormalizeDate(SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.BirthDate)));
            _workingResponse.Fields.Sex = FirstNonEmpty(chipFields.Sex, SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.Sex));
            _workingResponse.Fields.DocNo = FirstNonEmpty(chipFields.DocumentNumber, SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.DocumentNumber));
            _workingResponse.Fields.Nin = chipFields.PersonalNumber;
            _workingResponse.Fields.Address = chipFields.Address;
            _workingResponse.Fields.IssueDate = chipFields.IssueDate;
            _workingResponse.Fields.ExpiryDate = FirstNonEmpty(chipFields.ExpiryDate, NormalizeDate(SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.ExpiryDate)));

            if (_config.CurrentValue.IncludePhoto && string.IsNullOrWhiteSpace(_workingResponse.Images.PhotoBase64))
            {
                _workingResponse.Images.PhotoBase64 = TryImageAsJpegBase64(vizDoc, FieldSource.Viz, FieldId.Face);
            }
        }
    }

    private ChipAggregate BuildChipAggregateFields()
    {
        var aggregate = new ChipAggregate();

        foreach (var doc in _chipDocs.Values)
        {
            aggregate.FullNameAr = FirstNonEmpty(aggregate.FullNameAr, SafeFieldValue(doc, FieldSource.ECard, FieldId.Name));
            aggregate.Surname = FirstNonEmpty(aggregate.Surname, SafeFieldValue(doc, FieldSource.ECard, FieldId.Surname));
            aggregate.GivenName = FirstNonEmpty(aggregate.GivenName, SafeFieldValue(doc, FieldSource.ECard, FieldId.Name));
            aggregate.BirthDate = FirstNonEmpty(aggregate.BirthDate, NormalizeDate(SafeFieldValue(doc, FieldSource.ECard, FieldId.BirthDate)));
            aggregate.Sex = FirstNonEmpty(aggregate.Sex, SafeFieldValue(doc, FieldSource.ECard, FieldId.Sex));
            aggregate.DocumentNumber = FirstNonEmpty(aggregate.DocumentNumber, SafeFieldValue(doc, FieldSource.ECard, FieldId.DocumentNumber));
            aggregate.PersonalNumber = FirstNonEmpty(aggregate.PersonalNumber, SafeFieldValue(doc, FieldSource.ECard, FieldId.PersonalNumber));
            aggregate.Address = FirstNonEmpty(aggregate.Address, SafeFieldValue(doc, FieldSource.ECard, FieldId.Address));
            aggregate.IssueDate = FirstNonEmpty(aggregate.IssueDate, NormalizeDate(SafeFieldValue(doc, FieldSource.ECard, FieldId.IssueDate)));
            aggregate.ExpiryDate = FirstNonEmpty(aggregate.ExpiryDate, NormalizeDate(SafeFieldValue(doc, FieldSource.ECard, FieldId.ExpiryDate)));

            if (_config.CurrentValue.IncludePhoto && string.IsNullOrWhiteSpace(_workingResponse.Images.PhotoBase64))
            {
                _workingResponse.Images.PhotoBase64 = TryImageAsJpegBase64(doc, FieldSource.ECard, FieldId.Face);
            }
        }

        aggregate.SurnameGiven = string.Join(
            " ",
            new[] { aggregate.Surname, aggregate.GivenName }.Where(static x => !string.IsNullOrWhiteSpace(x))).Trim();

        return aggregate;
    }

    private static string BuildLatNameFromViz(Pr22.Processing.Document vizDoc)
    {
        var surname = SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.Surname);
        var givenName = SafeFieldValue(vizDoc, FieldSource.Mrz, FieldId.Name);
        return string.Join(" ", new[] { surname, givenName }.Where(static x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static bool HasVisualData(ReadResponse response)
    {
        return !string.IsNullOrWhiteSpace(response.Raw.Mrz)
               || !string.IsNullOrWhiteSpace(response.Raw.Barcode)
               || !string.IsNullOrWhiteSpace(response.Fields.DocNo)
               || !string.IsNullOrWhiteSpace(response.Fields.FullNameLat)
               || !string.IsNullOrWhiteSpace(response.Fields.Dob);
    }

    private static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var clean = value.Trim();

        if (DateTime.TryParse(clean, out var date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        if (clean.Length == 8 && DateTime.TryParseExact(clean, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        if (clean.Length == 6 && DateTime.TryParseExact(clean, "yyMMdd", null, System.Globalization.DateTimeStyles.None, out date))
        {
            return date.ToString("yyyy-MM-dd");
        }

        return clean;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string SafeFieldValue(Pr22.Processing.Document doc, FieldSource source, FieldId fieldId)
    {
        try
        {
            return doc.GetField(source, fieldId).GetBestStringValue();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryImageAsJpegBase64(Pr22.Processing.Document document, FieldSource source, FieldId fieldId)
    {
        _ = document;
        _ = source;
        _ = fieldId;
        return string.Empty;
    }

    private void OnConnection(object? sender, ConnectionEventArgs e)
    {
        if (e.DeviceNumber > 0)
        {
            _logger.LogInformation("Device connected event received. DeviceNumber={DeviceNumber}", e.DeviceNumber);
            if (!_deviceReady)
            {
                TryOpenConfiguredDevice();
            _started = true;
            }

            return;
        }

        _deviceReady = false;
        _logger.LogWarning("Device disconnected event received.");
    }

    private void OnAuthBegin(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthBegin {AuthProcess}", e.Authentication);
    }

    private void OnAuthFinished(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthFinished {AuthProcess} => {Result}", e.Authentication, e.Result);
        if (e.Result != ErrorCodes.ENOERR)
        {
            _authFailed = true;
        }
    }

    private void OnAuthWaitForInput(object? sender, AuthEventArgs e)
    {
        _logger.LogInformation("AuthWaitForInput {AuthProcess}", e.Authentication);

        try
        {
            BinData authData;
            if (!string.IsNullOrWhiteSpace(_mrzString))
            {
                authData = new BinData(Encoding.ASCII.GetBytes(_mrzString));
            }
            else if (!string.IsNullOrWhiteSpace(_canString))
            {
                authData = new BinData(Encoding.ASCII.GetBytes(_canString));
            }
            else
            {
                authData = e.Card.GetAuthReferenceData();
            }

            e.Card.Authenticate(e.Authentication, authData, 0);
        }
        catch (Exception ex)
        {
            _authFailed = true;
            _logger.LogError(ex, "Authentication callback failed for {Auth}", e.Authentication);
        }
    }

    private void OnReadBegin(object? sender, FileEventArgs e)
    {
        lock (_resultLock)
        {
            _requestedFiles.Add(e.FileId.ToString());
        }

        _logger.LogInformation("ReadBegin {FileId}", e.FileId);
    }

    private void OnReadFinished(object? sender, FileEventArgs e)
    {
        _logger.LogInformation("ReadFinished {FileId} => {Result}", e.FileId, e.Result);

        lock (_resultLock)
        {
            _receivedFiles.Add(e.FileId.ToString());
        }

        if (e.Result == ErrorCodes.ENOERR)
        {
            try
            {
                var raw = e.Card.GetFile(e.FileId);
                if (raw is not null && _pr is not null)
                {
                    var doc = _pr.Engine.Analyze(raw);
                    _chipDocs[e.FileId.ToString()] = doc;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file {FileId}", e.FileId);
            }
        }

        if (e.FileId.Id == (int)FileId.All && e.Result == ErrorCodes.ENOERR)
        {
            _currentReadTcs?.TrySetResult(true);
            return;
        }

        if (e.FileId.Id == (int)FileId.All && e.Result != ErrorCodes.ENOERR)
        {
            _currentReadTcs?.TrySetResult(true);
        }
    }

    private void OnFileChecked(object? sender, FileEventArgs e)
    {
        _logger.LogInformation("FileChecked {FileId} => {Result}", e.FileId, e.Result);
    }

    public void Dispose()
    {
        _readSemaphore.Dispose();
    }

    private sealed class ChipAggregate
    {
        public string FullNameAr { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string GivenName { get; set; } = string.Empty;
        public string SurnameGiven { get; set; } = string.Empty;
        public string BirthDate { get; set; } = string.Empty;
        public string Sex { get; set; } = string.Empty;
        public string DocumentNumber { get; set; } = string.Empty;
        public string PersonalNumber { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string IssueDate { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
    }
}
