using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using NPlayerServer.Models;

namespace NPlayerServer.Storage;

public static class ConfigStore
{
    /// <summary>
    /// 配置文件就放在 exe 旁边（程序当前目录），整个文件夹可以整体拷走。
    /// AppContext.BaseDirectory 在单文件发布下同样指向 exe 所在目录。
    /// </summary>
    public static string ConfigDirectory { get; } = AppContext.BaseDirectory;

    public static string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static bool _aclApplied;

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = AppConfig.CreateDefault();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg is null) return AppConfig.CreateDefault();

            cfg.Shares ??= new List<ShareEntry>();
            if (string.IsNullOrWhiteSpace(cfg.BindAddress)) cfg.BindAddress = "0.0.0.0";
            if (cfg.Port is < 1 or > 65535) cfg.Port = 8000;
            return cfg;
        }
        catch
        {
            return AppConfig.CreateDefault();
        }
    }

    public static bool Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, Options);
            File.WriteAllText(FilePath, json);
            if (!_aclApplied) _aclApplied = RestrictToCurrentUser(FilePath);
            return true;
        }
        catch
        {
            // 配置写不进去不应打断服务运行，但要让界面知道
            return false;
        }
    }

    private static bool RestrictToCurrentUser(string path)
    {
        try
        {
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            var psi = new ProcessStartInfo("icacls", $"\"{path}\" /inheritance:r /grant:r \"{user}:F\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
