using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using NPlayerServer.Models;

namespace NPlayerServer.Services;

internal sealed record DavItem(string Href, string Name, bool IsDir, long Size, DateTime Modified, string ContentType);

public sealed class WebDavServer : IDisposable
{
    private readonly AppConfig _config;
    private readonly LogBuffer _log;
    private readonly ShareResolver _resolver;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;
    private long _bytesSent;

    public WebDavServer(AppConfig config, LogBuffer log)
    {
        _config = config;
        _log = log;
        _resolver = new ShareResolver(config);
    }

    public bool IsRunning => _listener is not null;
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void Start()
    {
        if (_listener is not null) return;

        if (_config.Shares.Count == 0)
            throw new InvalidOperationException("没有配置任何共享目录");

        var address = _config.BindAddress == "0.0.0.0"
            ? IPAddress.Any
            : IPAddress.Parse(_config.BindAddress);

        var listener = new TcpListener(new IPEndPoint(address, _config.Port));
        listener.Start(64);

        _listener = listener;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);

        _log.Add($"服务已启动  {_config.BindAddress}:{_config.Port}  ·  {_config.Shares.Count} 个目录" +
                 (_config.Anonymous ? "  ·  无认证" : ""));
    }

    public void Stop()
    {
        if (_listener is null) return;

        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _log.Add("服务已停止");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch { continue; }

            _ = Task.Run(() => HandleConnectionAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            client.NoDelay = true;
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new BufferedStream(stream, 64 * 1024))
            {
                while (!token.IsCancellationRequested)
                {
                    HttpRequest? request;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(90));
                        try
                        {
                            request = await ReadRequestAsync(reader, idle.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    if (request is null) break;

                    bool keepAlive;
                    try
                    {
                        keepAlive = await HandleRequestAsync(request, stream, token).ConfigureAwait(false);
                    }
                    catch (IOException) { break; }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _log.Add($"请求出错  {ex.Message}");
                        break;
                    }

                    if (!keepAlive) break;
                }
            }
        }
        catch { }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private sealed class HttpRequest
    {
        public string Method = "";
        public string RawPath = "/";
        public string Version = "HTTP/1.1";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public bool WantsClose =>
            Version == "HTTP/1.0" ||
            (Headers.TryGetValue("Connection", out var c) && c.Contains("close", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var requestLine = await ReadLineAsync(stream, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requestLine)) return null;

        var parts = requestLine.Split(' ');
        if (parts.Length < 3) return null;

        var request = new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            RawPath = parts[1],
            Version = parts[2].ToUpperInvariant(),
        };

        while (true)
        {
            var line = await ReadLineAsync(stream, token).ConfigureAwait(false);
            if (line is null) return null;
            if (line.Length == 0) break;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (request.Headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var length) && length > 0 && length < 1024 * 1024)
        {
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, length - offset), token).ConfigureAwait(false);
                if (read <= 0) break;
                offset += read;
            }
        }

        return request;
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            buffer.Add(one[0]);
            if (buffer.Count > 32768) return null;
        }
    }

    private async Task<bool> HandleRequestAsync(HttpRequest request, NetworkStream output, CancellationToken token)
    {
        var raw = request.RawPath;
        var queryIndex = raw.IndexOf('?');
        if (queryIndex >= 0) raw = raw[..queryIndex];

        string path;
        try { path = Uri.UnescapeDataString(raw); }
        catch { path = raw; }

        if (!path.StartsWith('/')) path = "/" + path;

        if (!_config.Anonymous && !IsAuthorized(request))
        {
            await WriteSimpleAsync(output, 401, "Unauthorized", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("需要认证"),
                new[] { ("WWW-Authenticate", "Basic realm=\"nPlayer Server\", charset=\"UTF-8\"") },
                token).ConfigureAwait(false);
            _log.Add($"401  {request.Method} {path}");
            return true;
        }

        switch (request.Method)
        {
            case "OPTIONS":
                await WriteBareAsync(output,
                    "HTTP/1.1 200 OK\r\nDAV: 1, 2\r\nAllow: OPTIONS, GET, HEAD, PROPFIND\r\nMS-Author-Via: DAV\r\n" +
                    "Content-Length: 0\r\nConnection: keep-alive\r\n\r\n", token).ConfigureAwait(false);
                return true;

            case "PROPFIND":
                await HandlePropfindAsync(request, path, output, token).ConfigureAwait(false);
                return !request.WantsClose;

            case "GET":
            case "HEAD":
                await HandleGetAsync(request, path, output, request.Method == "HEAD", token).ConfigureAwait(false);
                return !request.WantsClose;

            default:
                await WriteSimpleAsync(output, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("只读服务"),
                    new[] { ("Allow", "OPTIONS, GET, HEAD, PROPFIND") },
                    token).ConfigureAwait(false);
                _log.Add($"405  {request.Method} {path}");
                return true;
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header)) return false;
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 0) return false;

            var user = decoded[..colon];
            var pass = decoded[(colon + 1)..];
            return string.Equals(user, _config.Username, StringComparison.Ordinal) &&
                   string.Equals(pass, _config.Password, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleGetAsync(HttpRequest request, string urlPath, NetworkStream output, bool headOnly, CancellationToken token)
    {
        var resolved = _resolver.Resolve(urlPath);

        if (resolved.Kind == ResolveKind.Root)
        {
            await WriteSimpleAsync(output, 200, "OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(BuildRootHtml()), null, token).ConfigureAwait(false);
            return;
        }

        if (resolved.Kind is ResolveKind.NotFound or ResolveKind.Forbidden || resolved.LocalPath is null)
        {
            await WriteNotFoundAsync(output, token).ConfigureAwait(false);
            return;
        }

        var local = resolved.LocalPath;

        if (Directory.Exists(local))
        {
            await WriteSimpleAsync(output, 200, "OK", "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(BuildDirectoryHtml(local, urlPath)), null, token).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(local))
        {
            await WriteNotFoundAsync(output, token).ConfigureAwait(false);
            return;
        }

        await StreamFileAsync(local, request, output, headOnly, token).ConfigureAwait(false);
    }

    private async Task StreamFileAsync(string localPath, HttpRequest request, NetworkStream output, bool headOnly, CancellationToken token)
    {
        FileStream file;
        try
        {
            file = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch
        {
            await WriteSimpleAsync(output, 403, "Forbidden", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("无法读取文件"), null, token).ConfigureAwait(false);
            return;
        }

        using (file)
        {
            long size = file.Length;
            long start = 0;
            long end = size > 0 ? size - 1 : 0;
            var partial = false;

            var hasRange = request.Headers.TryGetValue("Range", out var rangeHeader) && !string.IsNullOrWhiteSpace(rangeHeader);

            if (hasRange)
            {
                if (!TryParseRange(rangeHeader!, size, out start, out end))
                {
                    await WriteBareAsync(output,
                        "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" + size +
                        "\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n", token).ConfigureAwait(false);
                    return;
                }
                partial = true;
            }

            long length = size == 0 ? 0 : end - start + 1;

            var head = new StringBuilder(256);
            head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            head.Append("Content-Type: ").Append(ShareResolver.GetMime(localPath)).Append("\r\n");
            head.Append("Accept-Ranges: bytes\r\n");
            head.Append("Content-Length: ").Append(length).Append("\r\n");
            if (partial)
                head.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(size).Append("\r\n");
            head.Append("Last-Modified: ").Append(File.GetLastWriteTimeUtc(localPath).ToString("R")).Append("\r\n");
            head.Append("Connection: keep-alive\r\n\r\n");

            await output.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token).ConfigureAwait(false);

            _log.Add($"{(partial ? 206 : 200)}  GET {request.RawPath}  {ShareResolver.FormatSize(length)}");

            if (headOnly || length == 0) return;

            try
            {
                file.Seek(start, SeekOrigin.Begin);
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await file.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                        if (read <= 0) break;

                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        remaining -= read;
                        Interlocked.Add(ref _bytesSent, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    private static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0;
        end = size > 0 ? size - 1 : 0;

        var text = header.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        var spec = text[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;

        var startText = spec[..dash].Trim();
        var endText = spec[(dash + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start)) return false;
            if (endText.Length > 0)
            {
                if (!long.TryParse(endText, out var parsed)) return false;
                end = Math.Min(parsed, size - 1);
            }
            else
            {
                end = size - 1;
            }
        }

        if (size == 0 || start >= size || end < start) return false;
        return true;
    }

    private async Task HandlePropfindAsync(HttpRequest request, string urlPath, NetworkStream output, CancellationToken token)
    {
        var depth = request.Headers.TryGetValue("Depth", out var d) ? d.Trim() : "1";
        var includeChildren = depth != "0";

        var items = new List<DavItem>();
        var resolved = _resolver.Resolve(urlPath);

        if (resolved.Kind == ResolveKind.Root)
        {
            items.Add(new DavItem("/", "共享目录", true, 0, DateTime.Now, ""));
            if (includeChildren)
            {
                foreach (var share in _config.Shares)
                {
                    var modified = Directory.Exists(share.Path)
                        ? Directory.GetLastWriteTime(share.Path)
                        : DateTime.Now;
                    items.Add(new DavItem("/" + Uri.EscapeDataString(share.Alias) + "/", share.Alias, true, 0, modified, ""));
                }
            }
        }
        else if (resolved.Kind is ResolveKind.NotFound or ResolveKind.Forbidden || resolved.LocalPath is null)
        {
            await WriteNotFoundAsync(output, token).ConfigureAwait(false);
            return;
        }
        else
        {
            var local = resolved.LocalPath;
            var isDir = Directory.Exists(local);

            if (!isDir && !File.Exists(local))
            {
                await WriteNotFoundAsync(output, token).ConfigureAwait(false);
                return;
            }

            var href = BuildHref(urlPath, isDir);
            var name = Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = resolved.Share?.Alias ?? "共享目录";

            if (isDir)
            {
                items.Add(new DavItem(href, name, true, 0, Directory.GetLastWriteTime(local), ""));

                if (includeChildren)
                {
                    foreach (var entry in EnumerateSafe(local))
                    {
                        var entryIsDir = Directory.Exists(entry);
                        var entryName = Path.GetFileName(entry);
                        var childHref = href.TrimEnd('/') + "/" + Uri.EscapeDataString(entryName) + (entryIsDir ? "/" : "");

                        long entrySize = 0;
                        DateTime entryTime;
                        if (entryIsDir)
                        {
                            entryTime = Directory.GetLastWriteTime(entry);
                        }
                        else
                        {
                            try
                            {
                                var info = new FileInfo(entry);
                                entrySize = info.Length;
                                entryTime = info.LastWriteTime;
                            }
                            catch
                            {
                                entryTime = DateTime.Now;
                            }
                        }

                        items.Add(new DavItem(childHref, entryName, entryIsDir, entrySize, entryTime,
                            entryIsDir ? "" : ShareResolver.GetMime(entry)));
                    }
                }
            }
            else
            {
                var info = new FileInfo(local);
                items.Add(new DavItem(href, info.Name, false, info.Length, info.LastWriteTime, ShareResolver.GetMime(local)));
            }
        }

        var xml = BuildMultistatus(items);
        var bytes = Encoding.UTF8.GetBytes(xml);

        var head = new StringBuilder(160);
        head.Append("HTTP/1.1 207 Multi-Status\r\n");
        head.Append("Content-Type: application/xml; charset=\"utf-8\"\r\n");
        head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
        head.Append("Connection: keep-alive\r\n\r\n");

        await output.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token).ConfigureAwait(false);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);

        _log.Add($"207  PROPFIND {urlPath}  {items.Count} 项");
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        catch { return Array.Empty<string>(); }

        return entries.OrderBy(e =>
        {
            try { return Directory.Exists(e) ? 0 : 1; }
            catch { return 2; }
        }).ThenBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildHref(string urlPath, bool isDir)
    {
        var segments = urlPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return "/";

        var href = "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
        if (isDir && !href.EndsWith('/')) href += "/";
        return href;
    }

    private static string BuildMultistatus(IEnumerable<DavItem> items)
    {
        var sb = new StringBuilder(1024);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<D:multistatus xmlns:D=\"DAV:\">");

        foreach (var item in items)
        {
            sb.Append("<D:response><D:href>").Append(SecurityElement.Escape(item.Href)).Append("</D:href>");
            sb.Append("<D:propstat><D:prop>");
            sb.Append("<D:displayname>").Append(SecurityElement.Escape(item.Name)).Append("</D:displayname>");

            if (item.IsDir)
            {
                sb.Append("<D:resourcetype><D:collection/></D:resourcetype>");
            }
            else
            {
                sb.Append("<D:resourcetype/>");
                sb.Append("<D:getcontentlength>").Append(item.Size).Append("</D:getcontentlength>");
                if (!string.IsNullOrEmpty(item.ContentType))
                    sb.Append("<D:getcontenttype>").Append(SecurityElement.Escape(item.ContentType)).Append("</D:getcontenttype>");
            }

            sb.Append("<D:getlastmodified>").Append(item.Modified.ToUniversalTime().ToString("R")).Append("</D:getlastmodified>");
            sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }

        sb.Append("</D:multistatus>");
        return sb.ToString();
    }

    private string BuildRootHtml()
    {
        var sb = new StringBuilder();
        sb.Append(HtmlHead("共享目录"));
        sb.Append("<h1>共享目录</h1><table>");
        foreach (var share in _config.Shares)
        {
            var exists = Directory.Exists(share.Path);
            sb.Append("<tr><td><a href=\"/").Append(Uri.EscapeDataString(share.Alias)).Append("/\">")
              .Append(SecurityElement.Escape(share.Alias)).Append("/</a></td><td class=\"s\">")
              .Append(exists ? SecurityElement.Escape(share.Path) : "目录不存在")
              .Append("</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    private static string BuildDirectoryHtml(string directory, string urlPath)
    {
        var sb = new StringBuilder();
        var title = urlPath;
        sb.Append(HtmlHead(title));
        sb.Append("<h1>").Append(SecurityElement.Escape(title)).Append("</h1><table>");

        if (urlPath.Trim('/').Length > 0)
            sb.Append("<tr><td><a href=\"../\">../</a></td><td class=\"s\"></td></tr>");

        foreach (var entry in EnumerateSafe(directory))
        {
            var isDir = Directory.Exists(entry);
            var name = Path.GetFileName(entry);
            var suffix = isDir ? "/" : "";
            string sizeText = "";

            if (!isDir)
            {
                try { sizeText = ShareResolver.FormatSize(new FileInfo(entry).Length); }
                catch { sizeText = ""; }
            }

            sb.Append("<tr><td><a href=\"").Append(Uri.EscapeDataString(name)).Append(suffix).Append("\">")
              .Append(SecurityElement.Escape(name)).Append(suffix).Append("</a></td><td class=\"s\">")
              .Append(sizeText).Append("</td></tr>");
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string HtmlHead(string title) =>
        "<!doctype html><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<title>" + SecurityElement.Escape(title) + "</title>" +
        "<style>body{font-family:-apple-system,Segoe UI,sans-serif;margin:0;padding:20px;background:#fff;color:#222}" +
        "h1{font-size:16px;font-weight:500;margin:0 0 16px;color:#444;word-break:break-all}" +
        "table{border-collapse:collapse;width:100%}td{padding:10px 6px;border-bottom:1px solid #eee;font-size:14px}" +
        "a{color:#185FA5;text-decoration:none}.s{color:#999;text-align:right;font-size:12px;word-break:break-all}</style>";

    private async Task WriteNotFoundAsync(NetworkStream output, CancellationToken token) =>
        await WriteSimpleAsync(output, 404, "Not Found", "text/plain; charset=utf-8",
            Encoding.UTF8.GetBytes("Not Found"), null, token).ConfigureAwait(false);

    private static async Task WriteSimpleAsync(NetworkStream output, int code, string reason, string contentType,
        byte[] body, IEnumerable<(string, string)>? extraHeaders, CancellationToken token)
    {
        var sb = new StringBuilder(200);
        sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
                sb.Append(key).Append(": ").Append(value).Append("\r\n");
        }
        sb.Append("Connection: keep-alive\r\n\r\n");

        await output.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), token).ConfigureAwait(false);
        if (body.Length > 0) await output.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static async Task WriteBareAsync(NetworkStream output, string rawResponse, CancellationToken token) =>
        await output.WriteAsync(Encoding.UTF8.GetBytes(rawResponse), token).ConfigureAwait(false);

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
