namespace NPlayerServer.Models;

using System.Text.Json.Serialization;

public sealed class ShareEntry
{
    public string Path { get; set; } = "";
    public string Alias { get; set; } = "";

    public ShareEntry Clone() => new() { Path = Path, Alias = Alias };
}

public sealed class AppConfig
{
    public int Port { get; set; } = 8000;
    public string BindAddress { get; set; } = "0.0.0.0";
    public List<ShareEntry> Shares { get; set; } = new();
    public string Username { get; set; } = "user";
    public string Password { get; set; } = "";
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoStart { get; set; } = false;

    public bool EnableWebDav { get; set; } = true;
    public bool EnableFtp { get; set; } = false;
    public int FtpPort { get; set; } = 21;

    [JsonIgnore]
    public bool Anonymous => string.IsNullOrEmpty(Password);

    [JsonIgnore]
    public bool AnyProtocolEnabled => EnableWebDav || EnableFtp;

    public AppConfig Clone() => new()
    {
        Port = Port,
        BindAddress = BindAddress,
        Shares = Shares.Select(s => s.Clone()).ToList(),
        Username = Username,
        Password = Password,
        MinimizeToTray = MinimizeToTray,
        AutoStart = AutoStart,
        EnableWebDav = EnableWebDav,
        EnableFtp = EnableFtp,
        FtpPort = FtpPort,
    };

    public static AppConfig CreateDefault() => new()
    {
        Shares = new List<ShareEntry>
        {
            new() { Path = @"F:\夸克下载", Alias = "Videos" },
        },
    };
}
