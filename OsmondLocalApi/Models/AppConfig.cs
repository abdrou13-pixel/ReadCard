namespace OsmondLocalApi.Models;

public sealed class AppConfig
{
    public int Port { get; set; } = 8765;
    public int TimeoutSeconds { get; set; } = 10;
    public bool IncludePhoto { get; set; } = true;
    public string DeviceName { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string AuthLevel { get; set; } = "Opt";

    public Pr22.ECardHandling.AuthLevel GetAuthLevelOrDefault()
    {
        if (Enum.TryParse<Pr22.ECardHandling.AuthLevel>(AuthLevel, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return Pr22.ECardHandling.AuthLevel.Opt;
    }
}
