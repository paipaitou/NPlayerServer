using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NPlayerServer.Models;

namespace NPlayerServer.Services;

/// <summary>
/// 只读 FTP 服务：支持 PASV/EPSV 被动模式、LIST、RETR、REST 断点续传。
/// 中文文件名默认按系统 ANSI 编码发给客户端（兼容 Windows 资源管理器），
/// 客户端发 OPTS UTF8 ON 后切换为 UTF-8（nPlayer、FileZilla、WinSCP 都会发）。
/// </summary>
public sealed class FtpServer : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly Encoding AnsiEncoding;
    private static readonly ConcurrentDictionary<int, Encoding> StrictCache = new();

    static FtpServer()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            AnsiEncoding = Encoding.GetEncoding(0);
        }
        catch
        {
            AnsiEncoding = Utf8NoBom;
        }
    }

    private readonly AppConfig _config;
    private readonly LogBuffer _log;
    private readonly ShareResolver _resolver;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activeConnections;
    private long _bytesSent;

    public FtpServer(AppConfig config, LogBuffer log)
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
        if (_config.Shares.Count == 0) throw new InvalidOperationException("没有配置任何共享目录");

        var address = _config.BindAddress == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_config.BindAddress);
        var listener = new TcpListener(new IPEndPoint(address, _config.FtpPort));
        listener.Start(32);

        _listener = listener;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);

        _log.Add($"FTP 已启动  {_config.BindAddress}:{_config.FtpPort}" + (_config.Anonymous ? "  ·  允许匿名" : ""));
    }

    public void Stop()
    {
        if (_listener is null) return;

        try { _cts?.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _log.Add("FTP 已停止");
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

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private sealed class FtpSession
    {
        public string VirtualPath = "/";
        public bool Authenticated;
        public string PendingUser = string.Empty;
        public Encoding Encoder = AnsiEncoding;
        public long RestOffset;
        public TcpListener? Passive;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _activeConnections);
        var remote = "?";
        try
        {
            remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        }
        catch { }

        try
        {
            client.NoDelay = true;
            using (client)
            using (var stream = client.GetStream())
            {
                var session = new FtpSession();
                await ReplyAsync(stream, "220 nPlayer Server ready", token).ConfigureAwait(false);

                while (!token.IsCancellationRequested)
                {
                    byte[]? raw;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        idle.CancelAfter(TimeSpan.FromMinutes(5));
                        try
                        {
                            raw = await ReadLineBytesAsync(stream, idle.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    if (raw is null) break;

                    string line;
                    try { line = session.Encoder.GetString(raw); }
                    catch { line = Encoding.ASCII.GetString(raw); }

                    if (line.Length == 0) continue;

                    var keepAlive = await ExecuteAsync(line, session, stream, client, remote, token).ConfigureAwait(false);
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

    private async Task<bool> ExecuteAsync(string line, FtpSession session, NetworkStream stream,
        TcpClient client, string remote, CancellationToken token)
    {
        var space = line.IndexOf(' ');
        var cmd = (space < 0 ? line : line[..space]).Trim().ToUpperInvariant();
        var arg = space < 0 ? string.Empty : line[(space + 1)..].Trim();

        if (!session.Authenticated &&
            cmd is not ("USER" or "PASS" or "QUIT" or "SYST" or "FEAT" or "OPTS" or "NOOP" or "AUTH"))
        {
            await ReplyAsync(stream, "530 Please login with USER and PASS", token).ConfigureAwait(false);
            return true;
        }

        switch (cmd)
        {
            case "USER":
                session.PendingUser = arg;
                if (_config.Anonymous)
                {
                    session.Authenticated = true;
                    await ReplyAsync(stream, "230 Anonymous access granted", token).ConfigureAwait(false);
                }
                else
                {
                    await ReplyAsync(stream, "331 Password required", token).ConfigureAwait(false);
                }
                return true;

            case "PASS":
                if (session.Authenticated)
                {
                    await ReplyAsync(stream, "230 Already logged in", token).ConfigureAwait(false);
                    return true;
                }
                if (!_config.Anonymous &&
                    string.Equals(session.PendingUser, _config.Username, StringComparison.Ordinal) &&
                    string.Equals(arg, _config.Password, StringComparison.Ordinal))
                {
                    session.Authenticated = true;
                    await ReplyAsync(stream, "230 Logged in", token).ConfigureAwait(false);
                    _log.Add($"FTP 登录成功  {remote}  {session.PendingUser}");
                }
                else
                {
                    await ReplyAsync(stream, "530 Login incorrect", token).ConfigureAwait(false);
                    _log.Add($"FTP 登录失败  {remote}  {session.PendingUser}");
                }
                return true;

            case "SYST":
                await ReplyAsync(stream, "215 UNIX Type: L8", token).ConfigureAwait(false);
                return true;

            case "FEAT":
                await ReplyRawAsync(stream,
                    "211-Features:\r\n UTF8\r\n SIZE\r\n REST STREAM\r\n EPSV\r\n211 End\r\n", token).ConfigureAwait(false);
                return true;

            case "OPTS":
                if (arg.ToUpperInvariant().StartsWith("UTF8"))
                {
                    session.Encoder = Utf8NoBom;
                    await ReplyAsync(stream, "200 UTF8 mode enabled", token).ConfigureAwait(false);
                }
                else
                {
                    await ReplyAsync(stream, "200 OK", token).ConfigureAwait(false);
                }
                return true;

            case "NOOP":
                await ReplyAsync(stream, "200 OK", token).ConfigureAwait(false);
                return true;

            case "QUIT":
                await ReplyAsync(stream, "221 Goodbye", token).ConfigureAwait(false);
                return false;

            case "TYPE":
                await ReplyAsync(stream, "200 Type set to I", token).ConfigureAwait(false);
                return true;

            case "PWD":
            case "XPWD":
                await ReplyAsync(stream, $"257 \"{session.VirtualPath}\" is current directory", token).ConfigureAwait(false);
                return true;

            case "CWD":
            case "XCWD":
            {
                var target = ShareResolver.CombineVirtual(session.VirtualPath, arg);
                var resolved = ResolvePath(target, session);

                if (resolved.Kind == ResolveKind.Root)
                {
                    session.VirtualPath = "/";
                    await ReplyAsync(stream, "250 Directory changed", token).ConfigureAwait(false);
                }
                else if (resolved.Kind == ResolveKind.InShare && resolved.LocalPath is not null &&
                         Directory.Exists(resolved.LocalPath))
                {
                    // 用真实路径回填虚拟路径：模糊匹配命中时，后续 LIST/RETR 才会用对名字
                    session.VirtualPath = VirtualOf(resolved);
                    await ReplyAsync(stream, "250 Directory changed", token).ConfigureAwait(false);
                }
                else
                {
                    await ReplyAsync(stream, "550 Directory not found", token).ConfigureAwait(false);
                }
                return true;
            }

            case "CDUP":
            case "XCUP":
            {
                var parent = session.VirtualPath.TrimEnd('/');
                var index = parent.LastIndexOf('/');
                session.VirtualPath = index <= 0 ? "/" : parent[..index];
                await ReplyAsync(stream, "250 Directory changed", token).ConfigureAwait(false);
                return true;
            }

            case "PASV":
            {
                ClosePassive(session);
                var listener = new TcpListener(IPAddress.Any, 0);
                listener.Start(1);
                session.Passive = listener;

                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                var octets = local.ToString().Replace('.', ',');
                await ReplyAsync(stream,
                    $"227 Entering Passive Mode ({octets},{port / 256},{port % 256})", token).ConfigureAwait(false);
                return true;
            }

            case "EPSV":
            {
                ClosePassive(session);
                var listener = new TcpListener(IPAddress.Any, 0);
                listener.Start(1);
                session.Passive = listener;
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                await ReplyAsync(stream, $"229 Entering Extended Passive Mode (|||{port}|)", token).ConfigureAwait(false);
                return true;
            }

            case "LIST":
            case "NLST":
                await HandleListAsync(cmd == "NLST", arg, session, stream, remote, token).ConfigureAwait(false);
                return true;

            case "RETR":
                await HandleRetrAsync(arg, session, stream, remote, token).ConfigureAwait(false);
                return true;

            case "SIZE":
            {
                var resolved = ResolvePath(ShareResolver.CombineVirtual(session.VirtualPath, arg), session);
                if (resolved.Kind == ResolveKind.InShare && resolved.LocalPath is not null &&
                    File.Exists(resolved.LocalPath))
                {
                    var size = new FileInfo(resolved.LocalPath).Length;
                    await ReplyAsync(stream, $"213 {size}", token).ConfigureAwait(false);
                }
                else
                {
                    await ReplyAsync(stream, "550 File not found", token).ConfigureAwait(false);
                }
                return true;
            }

            case "REST":
                session.RestOffset = long.TryParse(arg, out var offset) && offset >= 0 ? offset : 0;
                await ReplyAsync(stream, $"350 Restarting at {session.RestOffset}", token).ConfigureAwait(false);
                return true;

            case "ABOR":
                ClosePassive(session);
                await ReplyAsync(stream, "226 Abort successful", token).ConfigureAwait(false);
                return true;

            case "STOR":
            case "APPE":
            case "DELE":
            case "MKD":
            case "XMKD":
            case "RMD":
            case "XRMD":
            case "RNFR":
            case "RNTO":
            case "SITE":
            case "CHMOD":
                await ReplyAsync(stream, "502 Read-only server", token).ConfigureAwait(false);
                return true;

            default:
                await ReplyAsync(stream, "502 Command not implemented", token).ConfigureAwait(false);
                return true;
        }
    }

    private async Task HandleListAsync(bool nameOnly, string arg, FtpSession session,
        NetworkStream control, string remote, CancellationToken token)
    {
        if (session.Passive is null)
        {
            await ReplyAsync(control, "425 Use PASV or EPSV first", token).ConfigureAwait(false);
            return;
        }

        // 有些客户端会发 "LIST -a" / "LIST -la"，把选项剥掉
        var listingArg = arg.Trim();
        if (listingArg.StartsWith('-'))
        {
            var space = listingArg.IndexOf(' ');
            listingArg = space < 0 ? string.Empty : listingArg[(space + 1)..].Trim();
        }

        var target = listingArg.Length == 0
            ? session.VirtualPath
            : ShareResolver.CombineVirtual(session.VirtualPath, listingArg);
        var resolved = ResolvePath(target, session);

        string text;
        if (resolved.Kind == ResolveKind.Root)
        {
            // 根目录：列出所有共享别名，客户端看到的是若干"文件夹"
            text = BuildRootListing(nameOnly);
        }
        else if (resolved.Kind == ResolveKind.InShare && resolved.LocalPath is not null &&
                 (Directory.Exists(resolved.LocalPath) || File.Exists(resolved.LocalPath)))
        {
            text = BuildListing(resolved.LocalPath, nameOnly);
        }
        else
        {
            ClosePassive(session);
            await ReplyAsync(control, "550 Path not found", token).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(control, "150 Here comes the directory listing", token).ConfigureAwait(false);

        TcpClient? dataClient = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try { dataClient = await session.Passive.AcceptTcpClientAsync(wait.Token).ConfigureAwait(false); }
            catch { }
        }

        ClosePassive(session);

        if (dataClient is null)
        {
            await ReplyAsync(control, "425 Cannot open data connection", token).ConfigureAwait(false);
            return;
        }

        try
        {
            using (dataClient)
            using (var data = dataClient.GetStream())
            {
                var bytes = session.Encoder.GetBytes(text);
                await data.WriteAsync(bytes, token).ConfigureAwait(false);
                Interlocked.Add(ref _bytesSent, bytes.Length);
            }

            await ReplyAsync(control, "226 Directory send OK", token).ConfigureAwait(false);
            _log.Add($"FTP LIST {target}  {remote}");
        }
        catch
        {
            try { await ReplyAsync(control, "426 Connection closed; transfer aborted", token).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task HandleRetrAsync(string arg, FtpSession session,
        NetworkStream control, string remote, CancellationToken token)
    {
        if (session.Passive is null)
        {
            await ReplyAsync(control, "425 Use PASV or EPSV first", token).ConfigureAwait(false);
            return;
        }

        var target = ShareResolver.CombineVirtual(session.VirtualPath, arg);
        var resolved = ResolvePath(target, session);

        if (resolved.Kind != ResolveKind.InShare || resolved.LocalPath is null || !File.Exists(resolved.LocalPath))
        {
            ClosePassive(session);
            await ReplyAsync(control, "550 File not found", token).ConfigureAwait(false);
            return;
        }

        var path = resolved.LocalPath;
        var offset = session.RestOffset;
        session.RestOffset = 0;

        await ReplyAsync(control, "150 Opening BINARY mode data connection", token).ConfigureAwait(false);

        TcpClient? dataClient = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try { dataClient = await session.Passive.AcceptTcpClientAsync(wait.Token).ConfigureAwait(false); }
            catch { }
        }

        ClosePassive(session);

        if (dataClient is null)
        {
            await ReplyAsync(control, "425 Cannot open data connection", token).ConfigureAwait(false);
            return;
        }

        FileStream? file = null;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (offset > 0 && offset < file.Length) file.Seek(offset, SeekOrigin.Begin);
            else if (offset >= file.Length) offset = 0;

            using (dataClient)
            using (var data = dataClient.GetStream())
            {
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    long sent = 0;
                    int read;
                    while ((read = await file.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
                    {
                        await data.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        sent += read;
                        Interlocked.Add(ref _bytesSent, read);
                    }

                    await ReplyAsync(control, "226 Transfer complete", token).ConfigureAwait(false);
                    _log.Add($"FTP RETR {target}  {ShareResolver.FormatSize(sent)}  {remote}");
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch
        {
            try { await ReplyAsync(control, "426 Transfer aborted", token).ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>
    /// 先按原样解析；失败时用"宽松匹配"兜底。
    /// 起因：夸克把文件名写成 `低📕智📕商`，Windows 资源管理器用 ANSI 编码，
    /// 📕 无法表示会被替换成 `?`，回传的 RETR 名字就对不上了。
    /// </summary>
    private ResolvedPath ResolvePath(string virtualPath, FtpSession session)
    {
        var strict = _resolver.Resolve(virtualPath);
        if (strict.Kind == ResolveKind.InShare && strict.LocalPath is not null &&
            (Directory.Exists(strict.LocalPath) || File.Exists(strict.LocalPath)))
            return strict;

        return TryResolveLoose(virtualPath, session.Encoder) ?? strict;
    }

    private ResolvedPath? TryResolveLoose(string virtualPath, Encoding encoder)
    {
        var segments = ShareResolver.NormalizeVirtualPath(virtualPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var share = _config.Shares.FirstOrDefault(s => LooseEquals(s.Alias, segments[0], encoder));
        if (share is null) return null;

        string current;
        try { current = Path.GetFullPath(share.Path); } catch { return null; }
        if (!Directory.Exists(current)) return null;

        for (var i = 1; i < segments.Length; i++)
        {
            var next = FindEntry(current, segments[i], encoder);
            if (next is null) return null;
            current = next;
        }

        return new ResolvedPath(ResolveKind.InShare, share, current);
    }

    private static string? FindEntry(string directory, string name, Encoding encoder)
    {
        try
        {
            var exact = Path.Combine(directory, name);
            if (Directory.Exists(exact) || File.Exists(exact)) return exact;

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (LooseEquals(Path.GetFileName(entry), name, encoder)) return entry;
            }
        }
        catch { }
        return null;
    }

    private static bool LooseEquals(string actual, string requested, Encoding encoder)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(requested)) return false;
        if (string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)) return true;
        if (!requested.Contains('?')) return false;

        return string.Equals(Skeleton(actual, encoder), Skeleton(requested, encoder),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>去掉编码不了的字（资源管理器看到的 `?`）和 `?` 本身，只比较能对上的部分。</summary>
    private static string Skeleton(string value, Encoding encoder)
    {
        var strict = Strict(encoder);
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '?') continue;
            try
            {
                strict.GetBytes(new[] { ch });
                sb.Append(ch);
            }
            catch { }
        }
        return sb.ToString();
    }

    private static Encoding Strict(Encoding encoder) =>
        StrictCache.GetOrAdd(encoder.CodePage, codePage =>
        {
            try
            {
                return Encoding.GetEncoding(codePage,
                    EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
            catch
            {
                return new UTF8Encoding(false, true);
            }
        });

    /// <summary>把解析结果换回 `/别名/子路径` 形式的虚拟路径。</summary>
    private static string VirtualOf(ResolvedPath resolved)
    {
        if (resolved.Share is null || resolved.LocalPath is null) return "/";

        var alias = (resolved.Share.Alias ?? string.Empty).Trim();
        string baseDir;
        try { baseDir = Path.GetFullPath(resolved.Share.Path).TrimEnd('\\', '/'); }
        catch { return "/" + alias; }

        var full = resolved.LocalPath;
        var relative = full.Length > baseDir.Length
            ? full[(baseDir.Length)..].Trim('\\', '/')
            : string.Empty;

        return relative.Length == 0
            ? "/" + alias
            : "/" + alias + "/" + relative.Replace('\\', '/');
    }

    /// <summary>根目录列表：每个共享别名伪装成一个目录，客户端可以直接双击进入。</summary>
    private string BuildRootListing(bool nameOnly)
    {
        var sb = new StringBuilder(256);

        foreach (var share in _config.Shares)
        {
            var alias = (share.Alias ?? string.Empty).Trim();
            if (alias.Length == 0)
                alias = Path.GetFileName(share.Path.TrimEnd('\\', '/'));

            if (nameOnly)
            {
                sb.Append(alias).Append("\r\n");
                continue;
            }

            var modified = DateTime.Now;
            try { if (Directory.Exists(share.Path)) modified = Directory.GetLastWriteTime(share.Path); }
            catch { }

            sb.Append("drwxr-xr-x").Append("    1 owner group ")
              .Append(0.ToString().PadLeft(12))
              .Append(' ')
              .Append(modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture))
              .Append(' ').Append(alias).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string BuildListing(string localPath, bool nameOnly)
    {
        var sb = new StringBuilder(1024);

        if (Directory.Exists(localPath))
        {
            foreach (var entry in EnumerateSafe(localPath))
            {
                var name = Path.GetFileName(entry);

                if (nameOnly)
                {
                    sb.Append(name).Append("\r\n");
                    continue;
                }

                var isDir = Directory.Exists(entry);
                long size = 0;
                DateTime modified;

                try
                {
                    if (isDir)
                    {
                        modified = Directory.GetLastWriteTime(entry);
                    }
                    else
                    {
                        var info = new FileInfo(entry);
                        size = info.Length;
                        modified = info.LastWriteTime;
                    }
                }
                catch
                {
                    modified = DateTime.Now;
                }

                var mode = isDir ? "drwxr-xr-x" : "-rw-r--r--";
                sb.Append(mode).Append("    1 owner group ")
                  .Append(size.ToString().PadLeft(12))
                  .Append(' ')
                  .Append(modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(name)
                  .Append("\r\n");
            }
        }
        else if (File.Exists(localPath))
        {
            var info = new FileInfo(localPath);
            if (nameOnly)
            {
                sb.Append(info.Name).Append("\r\n");
            }
            else
            {
                sb.Append("-rw-r--r--    1 owner group ")
                  .Append(info.Length.ToString().PadLeft(12))
                  .Append(' ')
                  .Append(info.LastWriteTime.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(info.Name)
                  .Append("\r\n");
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateSafe(string directory)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(directory); }
        catch { return Array.Empty<string>(); }

        return entries
            .OrderBy(e => { try { return Directory.Exists(e) ? 0 : 1; } catch { return 2; } })
            .ThenBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase);
    }

    private static void ClosePassive(FtpSession session)
    {
        try { session.Passive?.Stop(); } catch { }
        session.Passive = null;
    }

    private static async Task<byte[]?> ReadLineBytesAsync(Stream stream, CancellationToken token)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return buffer.Count == 0 ? null : buffer.ToArray();

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                return buffer.ToArray();
            }

            buffer.Add(one[0]);
            if (buffer.Count > 8192) return null;
        }
    }

    private static Task ReplyAsync(NetworkStream stream, string text, CancellationToken token) =>
        ReplyRawAsync(stream, text + "\r\n", token);

    private static async Task ReplyRawAsync(NetworkStream stream, string text, CancellationToken token) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).ConfigureAwait(false);

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
