using System.IO;
using NPlayerServer.Models;

namespace NPlayerServer.Services;

public enum ResolveKind { Root, InShare, NotFound, Forbidden }

public sealed record ResolvedPath(ResolveKind Kind, ShareEntry? Share, string? LocalPath);

/// <summary>
/// 把虚拟路径（/Videos/xxx.mkv）映射到本地文件系统路径。WebDAV 和 FTP 共用。
/// </summary>
public sealed class ShareResolver
{
    private readonly AppConfig _config;

    public ShareResolver(AppConfig config) => _config = config;

    public ResolvedPath Resolve(string urlPath)
    {
        var segments = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return new ResolvedPath(ResolveKind.Root, null, null);

        var share = _config.Shares.FirstOrDefault(s =>
            string.Equals(s.Alias, segments[0], StringComparison.OrdinalIgnoreCase));
        if (share is null) return new ResolvedPath(ResolveKind.NotFound, null, null);

        string baseDir;
        try { baseDir = Path.GetFullPath(share.Path); }
        catch { return new ResolvedPath(ResolveKind.NotFound, share, null); }

        var rest = segments.Skip(1).ToArray();
        string combined;
        try
        {
            combined = rest.Length == 0
                ? baseDir
                : Path.GetFullPath(Path.Combine(baseDir, string.Join(Path.DirectorySeparatorChar, rest)));
        }
        catch
        {
            return new ResolvedPath(ResolveKind.Forbidden, share, null);
        }

        if (!IsInside(combined, baseDir)) return new ResolvedPath(ResolveKind.Forbidden, share, null);
        return new ResolvedPath(ResolveKind.InShare, share, combined);
    }

    /// <summary>把 FTP 会话里的虚拟路径规范化成 /A/B 形式。</summary>
    public static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";

        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }

        return "/" + string.Join("/", parts);
    }

    public static string CombineVirtual(string basePath, string relative)
    {
        if (relative.StartsWith('/') || relative.StartsWith('\\')) return NormalizeVirtualPath(relative);
        return NormalizeVirtualPath(basePath.TrimEnd('/') + "/" + relative);
    }

    public static bool IsInside(string candidate, string baseDir)
    {
        var b = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var c = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(c, b, StringComparison.OrdinalIgnoreCase)) return true;
        return c.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "video/x-matroska",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".ts"] = "video/mp2t",
        [".avi"] = "video/x-msvideo",
        [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        [".webm"] = "video/webm",
        [".mpg"] = "video/mpeg",
        [".mpeg"] = "video/mpeg",
        [".rmvb"] = "video/vnd.rn-realvideo",
        [".rm"] = "application/vnd.rn-realmedia",
        [".srt"] = "application/x-subrip",
        [".ass"] = "text/x-ssa",
        [".ssa"] = "text/x-ssa",
        [".vtt"] = "text/vtt",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".html"] = "text/html; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
    };

    public static string GetMime(string path) =>
        MimeMap.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double value = bytes / 1024.0;
        if (value < 1024) return value.ToString("0.0") + " KB";
        value /= 1024.0;
        if (value < 1024) return value.ToString("0.0") + " MB";
        value /= 1024.0;
        return value.ToString("0.00") + " GB";
    }
}
