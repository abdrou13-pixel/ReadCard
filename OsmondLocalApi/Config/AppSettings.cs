namespace OsmondLocalApi.Config;

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public int TimeoutSeconds { get; set; } = 10;
    public bool IncludePhoto { get; set; } = true;
    public string DeviceName { get; set; } = "Osmond R V2 SN1234";
    public string? ApiKey { get; set; } = "optional-api-key";
}
