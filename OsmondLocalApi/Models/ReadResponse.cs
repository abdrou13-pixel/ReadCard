using System.Text.Json.Serialization;

namespace OsmondLocalApi.Models;

public enum ResponseCode
{
    Ok,
    DeviceNotFound,
    DeviceOpenFailed,
    NoDocument,
    AuthFailed,
    ChipReadFailed,
    ReadFailed,
    Timeout,
    ReadInProgress,
    Unauthorized
}

public sealed class ReadResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("code")]
    public string Code => InternalCode switch
    {
        ResponseCode.Ok => "OK",
        ResponseCode.DeviceNotFound => "DEVICE_NOT_FOUND",
        ResponseCode.DeviceOpenFailed => "DEVICE_OPEN_FAILED",
        ResponseCode.NoDocument => "NO_DOCUMENT",
        ResponseCode.AuthFailed => "AUTH_FAILED",
        ResponseCode.ChipReadFailed => "CHIP_READ_FAILED",
        ResponseCode.ReadFailed => "READ_FAILED",
        ResponseCode.Timeout => "TIMEOUT",
        ResponseCode.ReadInProgress => "READ_IN_PROGRESS",
        ResponseCode.Unauthorized => "UNAUTHORIZED",
        _ => "READ_FAILED"
    };

    [JsonIgnore]
    public ResponseCode InternalCode { get; set; } = ResponseCode.Ok;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("fields")]
    public ReadFields Fields { get; set; } = new();

    [JsonPropertyName("raw")]
    public RawPayload Raw { get; set; } = new();

    [JsonPropertyName("images")]
    public ImagePayload Images { get; set; } = new();

    public static ReadResponse Failure(ResponseCode code, string message) => new()
    {
        Ok = false,
        InternalCode = code,
        Message = message
    };
}

public sealed class ReadFields
{
    [JsonPropertyName("full_name_ar")]
    public string FullNameAr { get; set; } = string.Empty;

    [JsonPropertyName("full_name_lat")]
    public string FullNameLat { get; set; } = string.Empty;

    [JsonPropertyName("dob")]
    public string Dob { get; set; } = string.Empty;

    [JsonPropertyName("sex")]
    public string Sex { get; set; } = string.Empty;

    [JsonPropertyName("doc_no")]
    public string DocNo { get; set; } = string.Empty;

    [JsonPropertyName("nin")]
    public string Nin { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("issue_date")]
    public string IssueDate { get; set; } = string.Empty;

    [JsonPropertyName("expiry_date")]
    public string ExpiryDate { get; set; } = string.Empty;
}

public sealed class RawPayload
{
    [JsonPropertyName("mrz")]
    public string Mrz { get; set; } = string.Empty;

    [JsonPropertyName("barcode")]
    public string Barcode { get; set; } = string.Empty;
}

public sealed class ImagePayload
{
    [JsonPropertyName("photo_base64")]
    public string PhotoBase64 { get; set; } = string.Empty;

    [JsonPropertyName("photo_mime")]
    public string PhotoMime { get; set; } = "image/jpeg";
}
