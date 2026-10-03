using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Services;

namespace FullStackLauncher.ProjectTasks;

/// <summary>Source captured from the live, selected Chrome or Edge tab near the screen snip.</summary>
internal sealed record BrowserPageContext(string Url, string Html, string Css, string Status);

internal sealed record BrowserWindowBounds(int Left, int Top, int Width, int Height, int Dpi);

internal enum BrowserSiteOpenState { Opened, AlreadyOpen, Unavailable }

/// <summary>A bounded, URL-free outcome; tab contents are never requested by site opening.</summary>
internal sealed record BrowserSiteOpenResult(BrowserSiteOpenState State, string Status);

/// <summary>
/// Receives live page source from the separately installed browser extension.
/// The extension proves it knows this Windows user's pairing code before reading a page.
/// </summary>
internal static class BrowserPageCaptureBroker
{
    private const int Port = 47873;
    private const int MaximumHtmlChars = 200_000;
    private const int MaximumCssChars = 200_000;
    private const string ExtensionOrigin = "chrome-extension://poppjfplkbpgbabkabcbhdaifmbfcijf";
    private const string ProtectionPurpose = "FullStackLauncher.BrowserCapture.Pairing.v1";
    private static readonly string SecretPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullStackLauncher", "browser-capture-pairing.dat");
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private static readonly List<BrowserConnection> Connections = [];
    private static long _connectionRevision;
    private static CancellationTokenSource? _siteOpenCancellation;
    private static TcpListener? _listener;
    private static string? _startupError;
    private static byte[]? _secret;

    /// <summary>Display only in the explicit browser-source setup dialog; never log or save in notes.</summary>
    public static string GetPairingCode()
    {
        var code = Convert.ToHexString(GetSecret()).ToLowerInvariant();
        EnsureListening();
        return code;
    }

    /// <summary>Starts authentication only; does not inspect tabs, capture content or open a browser.</summary>
    public static void PrepareSiteAccess()
    {
        try { GetSecret(); EnsureListening(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // The eventual site request reports a bounded, value-free failure.
        }
    }

    /// <summary>
    /// Checks every authenticated Chrome/Edge profile before opening one local site tab.
    /// Native normal/popup window counts must cover every browser profile, including minimized
    /// and incognito windows. A count mismatch or unsupported/unreadable inventory must fail
    /// closed in the caller. Counts cannot prove completeness for hidden or unsupported windows.
    /// The optional native refresh rejects windows opened after the initial inventory.
    /// </summary>
    public static async Task<BrowserSiteOpenResult> EnsureSiteOpenAsync(
        string url, IReadOnlyDictionary<string, int> expectedBrowserWindows,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, int>?>>? refreshBrowserWindows = null)
    {
        if (!IsLocalSiteUrl(url) || !IsValidWindowInventory(expectedBrowserWindows))
            return SiteUnavailable("Automatic open skipped: the local address or browser inventory is unavailable.");
        try
        {
            GetSecret();
            EnsureListening();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return SiteUnavailable("Automatic open skipped: browser pairing could not be read.");
        }
        if (_startupError != null)
            return SiteUnavailable("Automatic open skipped: the browser connection could not start.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var acquired = false;
        try
        {
            await CaptureGate.WaitAsync(timeout.Token);
            acquired = true;
            BrowserConnection[] connections;
            long revision;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            do
            {
                lock (Gate)
                {
                    connections = Connections.Where(item => item.IsOpen).ToArray();
                    revision = _connectionRevision;
                }
                if (connections.Length > 0) break;
                await Task.Delay(100, timeout.Token);
            } while (DateTime.UtcNow < deadline);
            if (connections.Length == 0)
                return SiteUnavailable("Automatic open skipped: no paired browser extension is connected.");

            var first = await ReadSiteInventoryAsync(connections, url, timeout.Token);
            if (first.Any(item => item.State == "found")) return SiteAlreadyOpen();
            if (!InventoryIsComplete(connections, first, expectedBrowserWindows, revision))
                return SiteUnavailable("Automatic open skipped: not every browser window has a current paired tab inventory.");

            // Recheck all profiles, not only the chosen profile. Other launcher requests share
            // CaptureGate; extension revisions also reject a tab/window change during this check.
            if (refreshBrowserWindows != null)
            {
                var refreshed = await refreshBrowserWindows(timeout.Token).WaitAsync(timeout.Token);
                if (refreshed == null || !SameWindowInventory(expectedBrowserWindows, refreshed))
                    return SiteUnavailable("Automatic open skipped: browser windows changed or could not be inspected.");
            }
            var second = await ReadSiteInventoryAsync(connections, url, timeout.Token);
            if (second.Any(item => item.State == "found")) return SiteAlreadyOpen();
            if (!InventoryIsComplete(connections, second, expectedBrowserWindows, revision))
                return SiteUnavailable("Automatic open skipped: browser tabs or profiles changed during inspection.");

            if (refreshBrowserWindows != null)
            {
                var refreshed = await refreshBrowserWindows(timeout.Token).WaitAsync(timeout.Token);
                if (refreshed == null || !SameWindowInventory(expectedBrowserWindows, refreshed) ||
                    !InventoryIsComplete(connections, second, expectedBrowserWindows, revision))
                    return SiteUnavailable("Automatic open skipped: browser windows changed before opening.");
            }
            var targetIndex = Array.FindIndex(second, item => item.WindowCount > 0);
            if (targetIndex < 0)
                return SiteUnavailable("Automatic open skipped: no paired browser window is available.");
            using var openingCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            void CancelForInventoryChange()
            {
                try { openingCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            var otherProfiles = connections.Where((_, index) => index != targetIndex).ToArray();
            foreach (var profile in otherProfiles) profile.SiteChanged += CancelForInventoryChange;
            SiteResponse result;
            try
            {
                lock (Gate) _siteOpenCancellation = openingCancellation;
                if (!InventoryIsComplete(connections, second, expectedBrowserWindows, revision))
                    return SiteUnavailable("Automatic open skipped: browser profiles changed before opening.");
                result = await connections[targetIndex].RequestSiteAsync("ensureSiteOpen", url,
                    second[targetIndex].Revision, openingCancellation.Token);
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                return SiteUnavailable("Automatic open skipped: another browser profile changed during opening.");
            }
            finally
            {
                lock (Gate) _siteOpenCancellation = null;
                foreach (var profile in otherProfiles) profile.SiteChanged -= CancelForInventoryChange;
            }
            return result.State switch
            {
                "opened" => new(BrowserSiteOpenState.Opened, "Opened frontend after the successful build."),
                "found" => SiteAlreadyOpen(),
                _ => SiteUnavailable("Automatic open skipped: browser inventory changed or the browser could not open the site.")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SiteUnavailable("Automatic open skipped: browser inspection timed out.");
        }
        catch (Exception error) when (error is TimeoutException or IOException or WebSocketException or InvalidOperationException)
        {
            return SiteUnavailable("Automatic open skipped: a browser connection closed or did not respond.");
        }
        finally { if (acquired) CaptureGate.Release(); }
    }

    private static BrowserSiteOpenResult SiteUnavailable(string status) => new(BrowserSiteOpenState.Unavailable, status);
    private static BrowserSiteOpenResult SiteAlreadyOpen() =>
        new(BrowserSiteOpenState.AlreadyOpen, "Frontend is already open; no additional tab was opened.");

    private static bool IsLocalSiteUrl(string url) => !string.IsNullOrWhiteSpace(url) && url.Length <= 4096 &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Trim('[', ']').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";

    private static bool IsValidWindowInventory(IReadOnlyDictionary<string, int> windows) =>
        windows.All(item => item.Key is "chrome" or "msedge" && item.Value is >= 0 and <= 1024) &&
        windows.Values.Sum() > 0;

    private static bool SameWindowInventory(IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual) => IsValidWindowInventory(actual) &&
        new[] { "chrome", "msedge" }.All(browser => expected.GetValueOrDefault(browser) == actual.GetValueOrDefault(browser));

    private static async Task<SiteResponse[]> ReadSiteInventoryAsync(BrowserConnection[] connections,
        string url, CancellationToken cancellationToken) => await Task.WhenAll(connections.Select(connection =>
            connection.RequestSiteAsync("siteInventory", url, null, cancellationToken)));

    private static bool InventoryIsComplete(BrowserConnection[] connections, SiteResponse[] inventory,
        IReadOnlyDictionary<string, int> expectedBrowserWindows, long revision)
    {
        lock (Gate)
        {
            if (_connectionRevision != revision || connections.Length != inventory.Length ||
                connections.Any(item => !item.IsOpen || !item.SupportsSiteOpening)) return false;
            for (var index = 0; index < inventory.Length; index++)
                if (inventory[index].State != "absent" || connections[index].SiteRevision != inventory[index].Revision)
                    return false;
            return new[] { "chrome", "msedge" }.All(browser =>
                connections.Select((connection, index) => (connection, index))
                    .Where(item => item.connection.Browser == browser).Sum(item => inventory[item.index].WindowCount) ==
                expectedBrowserWindows.GetValueOrDefault(browser));
        }
    }

    public static async Task<BrowserPageContext> CaptureAsync(
        string browser, string expectedTitle, string? expectedUrl, BrowserWindowBounds expectedBounds)
    {
        if (browser is not ("chrome" or "msedge") || string.IsNullOrWhiteSpace(expectedTitle))
            return Unavailable("Browser tab identity was unavailable. Choose the tab and snip again.");
        try
        {
            GetSecret();
            EnsureListening();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return Unavailable("Browser source setup could not read its protected pairing code. Review browser source setup.");
        }

        if (_startupError != null)
            return Unavailable("Browser source connection could not start. Another launcher or app may be using local port 47873.");

        await CaptureGate.WaitAsync();
        try
        {
            BrowserConnection? connection = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                lock (Gate)
                {
                    var matching = Connections.Where(item => item.Browser == browser && item.IsOpen).ToArray();
                    if (matching.Length > 1)
                        return Unavailable("More than one paired browser extension is connected. Close extra browser profiles and snip again.");
                    connection = matching.SingleOrDefault();
                }
                if (connection != null) break;
                await Task.Delay(100);
            }
            if (connection == null)
                return Unavailable("Browser source unavailable. Install and pair the Browser Capture extension in this browser.");

            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<BrowserPageContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.SetPending(requestId, expectedTitle, expectedBounds, completion);
            try
            {
                await connection.SendAsync(new
                {
                    type = "capture", requestId, expectedTitle,
                    expectedUrl = expectedUrl ?? "", browser,
                    maxHtmlChars = MaximumHtmlChars, maxCssChars = MaximumCssChars
                });
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                return Unavailable("Browser extension did not return page source before the capture timeout.");
            }
            catch (Exception error) when (error is IOException or WebSocketException or InvalidOperationException)
            {
                return Unavailable("Browser source connection closed before capture finished.");
            }
            finally { connection.ClearPending(requestId); }
        }
        finally { CaptureGate.Release(); }
    }

    private static BrowserPageContext Unavailable(string status) => new("", "", "", status);

    private static byte[] GetSecret()
    {
        lock (Gate)
        {
            if (_secret != null) return _secret;
            if (File.Exists(SecretPath))
            {
                var protectedBytes = File.ReadAllBytes(SecretPath);
                var secret = WindowsUserSecretProtection.Unprotect(protectedBytes, ProtectionPurpose);
                if (secret.Length != 32) throw new CryptographicException("Invalid browser capture pairing code.");
                _secret = secret;
                return secret;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SecretPath)!);
            var fresh = RandomNumberGenerator.GetBytes(32);
            var protectedFresh = WindowsUserSecretProtection.Protect(fresh, ProtectionPurpose);
            var temporaryPath = SecretPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, protectedFresh);
                try { File.Move(temporaryPath, SecretPath); }
                catch (IOException) when (File.Exists(SecretPath))
                {
                    var winner = WindowsUserSecretProtection.Unprotect(File.ReadAllBytes(SecretPath), ProtectionPurpose);
                    if (winner.Length != 32) throw new CryptographicException("Invalid browser capture pairing code.");
                    _secret = winner;
                    return winner;
                }
                _secret = fresh;
                return fresh;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void EnsureListening()
    {
        lock (Gate)
        {
            if (_listener != null) return;
            // Another launcher may have released the port since the last snip.
            _startupError = null;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start(4);
                _listener = listener;
                _ = AcceptLoopAsync(listener);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                _startupError = "Local browser source port is unavailable.";
            }
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException)
            {
                lock (Gate) { _startupError = "Local browser source listener stopped."; _listener = null; }
                return;
            }
            _ = HandleClientAsync(client);
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            try
            {
                var stream = client.GetStream();
                using var handshakeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (!await CompleteHandshakeAsync(stream, handshakeTimeout.Token)) return;
                using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(20));
                using var hello = await ReceiveJsonAsync(socket, handshakeTimeout.Token);
                if (hello == null || ReadString(hello.RootElement, "type") != "hello" ||
                    ReadInt(hello.RootElement, "version") != 1 ||
                    ReadString(hello.RootElement, "extensionId") != "poppjfplkbpgbabkabcbhdaifmbfcijf") return;
                var browser = ReadString(hello.RootElement, "browser");
                var challenge = ReadString(hello.RootElement, "challenge");
                if (browser is not ("chrome" or "msedge") || !IsValidChallenge(challenge)) return;
                var signed = HMACSHA256.HashData(GetSecret(), Encoding.UTF8.GetBytes("server:" + challenge));
                var serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await SendJsonAsync(socket, new
                {
                    type = "proof", challenge, signature = Convert.ToBase64String(signed), serverNonce
                }, handshakeTimeout.Token);
                using var ready = await ReceiveJsonAsync(socket, handshakeTimeout.Token);
                if (ready == null || ReadString(ready.RootElement, "type") != "ready" ||
                    !VerifyReady(ReadString(ready.RootElement, "signature"), serverNonce)) return;

                var connection = new BrowserConnection(browser, socket,
                    ReadInt(hello.RootElement, "siteOpenVersion") == 1);
                lock (Gate) { Connections.Add(connection); _connectionRevision++; _siteOpenCancellation?.Cancel(); }
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = await ReceiveJsonAsync(socket, CancellationToken.None);
                        if (message == null) break;
                        switch (ReadString(message.RootElement, "type"))
                        {
                            case "captureResult": connection.Complete(message.RootElement); break;
                            case "siteResult": connection.CompleteSite(message.RootElement); break;
                            case "siteInventoryChanged": connection.InventoryChanged(message.RootElement); break;
                        }
                    }
                }
                finally
                {
                    lock (Gate) { Connections.Remove(connection); _connectionRevision++; _siteOpenCancellation?.Cancel(); }
                    connection.Disconnect();
                }
            }
            catch (Exception error) when (error is IOException or SocketException or WebSocketException or
                OperationCanceledException or JsonException or InvalidOperationException or CryptographicException or
                UnauthorizedAccessException or ArgumentException)
            {
                // An extension can disconnect or send malformed input at any time.
            }
        }
    }

    private static async Task<bool> CompleteHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(one, cancellationToken) != 1) return false;
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                break;
        }
        if (bytes.Count >= 8192) return false;
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2 || lines[0] != "GET /browser-capture HTTP/1.1") return false;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (!headers.TryGetValue("Origin", out var origin) || origin != ExtensionOrigin ||
            !headers.TryGetValue("Host", out var host) || host is not ("127.0.0.1:47873" or "localhost:47873") ||
            !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
            !headers.TryGetValue("Connection", out var connection) ||
            !connection.Split(',').Any(token => token.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) ||
            !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13" ||
            !headers.TryGetValue("Sec-WebSocket-Key", out var key)) return false;
        try { if (Convert.FromBase64String(key).Length != 16) return false; }
        catch (FormatException) { return false; }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
        return true;
    }

    private static async Task<JsonDocument?> ReceiveJsonAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (output.Length <= 2_000_000)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            if (received.MessageType != WebSocketMessageType.Text) return null;
            output.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                if (output.Length > 2_000_000) return null;
                return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            }
        }
        return null;
    }

    private static async Task SendJsonAsync(WebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : 0;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : -1;

    private static bool IsValidChallenge(string challenge)
    {
        try { return Convert.FromBase64String(challenge).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool VerifyReady(string signature, string serverNonce)
    {
        try
        {
            var actual = Convert.FromBase64String(signature);
            var expected = HMACSHA256.HashData(GetSecret(), Encoding.UTF8.GetBytes("client:" + serverNonce));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private sealed record SiteResponse(string State, int WindowCount, long Revision);

    private sealed class BrowserConnection(string browser, WebSocket socket, bool supportsSiteOpening)
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _siteGate = new();
        private string? _requestId;
        private string? _expectedTitle;
        private BrowserWindowBounds? _expectedBounds;
        private TaskCompletionSource<BrowserPageContext>? _pending;
        private string? _siteRequestId;
        private TaskCompletionSource<SiteResponse>? _sitePending;
        private long _siteRevision;

        public string Browser { get; } = browser;
        public bool IsOpen => socket.State == WebSocketState.Open;
        public bool SupportsSiteOpening { get; } = supportsSiteOpening;
        public long SiteRevision => Interlocked.Read(ref _siteRevision);
        public event Action? SiteChanged;

        public void SetPending(string requestId, string expectedTitle, BrowserWindowBounds expectedBounds,
            TaskCompletionSource<BrowserPageContext> pending)
        {
            _requestId = requestId;
            _expectedTitle = expectedTitle;
            _expectedBounds = expectedBounds;
            _pending = pending;
        }

        public async Task SendAsync(object message, CancellationToken cancellationToken = default)
        {
            await _sendGate.WaitAsync(cancellationToken);
            try { await SendJsonAsync(socket, message, cancellationToken); }
            finally { _sendGate.Release(); }
        }

        public async Task<SiteResponse> RequestSiteAsync(string type, string url, long? expectedRevision,
            CancellationToken cancellationToken)
        {
            if (!SupportsSiteOpening || !IsOpen) return new("unavailable", 0, -1);
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<SiteResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_siteGate) { _siteRequestId = requestId; _sitePending = completion; }
            try
            {
                await SendAsync(new
                {
                    type, requestId, url, expectedRevision,
                    notAfterUtc = DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeMilliseconds()
                }, cancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            finally
            {
                if (type == "ensureSiteOpen" && (!completion.Task.IsCompletedSuccessfully || cancellationToken.IsCancellationRequested))
                {
                    // The extension also enforces a deadline and removes only a tab created by
                    // this canceled request if cancellation arrives during tabs.create.
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                    try { await SendAsync(new { type = "cancelSiteOpen", requestId }, cancelTimeout.Token); }
                    catch (Exception error) when (error is OperationCanceledException or IOException or WebSocketException or InvalidOperationException) { }
                }
                lock (_siteGate)
                {
                    if (_siteRequestId == requestId) { _siteRequestId = null; _sitePending = null; }
                }
            }
        }

        public void CompleteSite(JsonElement result)
        {
            var state = ReadString(result, "status");
            var windowCount = ReadInt(result, "windowCount");
            var revision = ReadLong(result, "revision");
            if (state is not ("found" or "absent" or "opened") || windowCount is < 0 or > 1024 || revision < 0)
                state = "unavailable";
            lock (_siteGate)
            {
                if (_sitePending == null || ReadString(result, "requestId") != _siteRequestId) return;
                if (revision > _siteRevision) Interlocked.Exchange(ref _siteRevision, revision);
                _sitePending.TrySetResult(new SiteResponse(state, windowCount, revision));
            }
        }

        public void InventoryChanged(JsonElement message)
        {
            var revision = ReadLong(message, "revision");
            var changed = false;
            lock (_siteGate)
                if (revision > _siteRevision) { Interlocked.Exchange(ref _siteRevision, revision); changed = true; }
            if (changed) SiteChanged?.Invoke();
        }

        public void Complete(JsonElement result)
        {
            if (_pending == null || ReadString(result, "requestId") != _requestId) return;
            var status = ReadString(result, "status");
            var title = ReadString(result, "title");
            var url = ReadString(result, "url");
            var issues = result.TryGetProperty("issues", out var reportedIssues) &&
                reportedIssues.ValueKind == JsonValueKind.Array
                ? reportedIssues.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))
                    .Take(2).Select(item => item!.Length > 90 ? item[..90] : item).ToArray()
                : [];
            if (status is not ("captured" or "partial"))
            {
                var reason = status == "mismatch"
                    ? "Browser tab changed before page source capture. Snip the tab again."
                    : issues.Length > 0 ? string.Join(" ", issues)
                    : "Browser page source was unavailable for this tab.";
                if (reason.Length > ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters)
                    reason = reason[..ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters];
                _pending.TrySetResult(Unavailable(reason));
                return;
            }
            // The authenticated extension already compared canonical URL forms.
            // Raw .NET and Chromium URL serialization can differ harmlessly.
            if (title != _expectedTitle ||
                _expectedBounds == null || !MatchesWindowBounds(result, _expectedBounds))
            {
                _pending.TrySetResult(Unavailable("Browser tab changed before page source capture. Snip the tab again."));
                return;
            }
            var html = ReadString(result, "html");
            var css = ReadString(result, "css");
            if (string.IsNullOrWhiteSpace(html) && string.IsNullOrWhiteSpace(css))
            {
                _pending.TrySetResult(Unavailable("Browser extension returned no usable page source."));
                return;
            }
            var truncated = html.Length > MaximumHtmlChars || css.Length > MaximumCssChars;
            if (html.Length > MaximumHtmlChars) html = html[..MaximumHtmlChars];
            if (css.Length > MaximumCssChars) css = css[..MaximumCssChars];
            var description = truncated || status == "partial"
                ? "Live page source captured partially. " +
                  (issues.Length == 0 ? "Some HTML or CSS was unavailable or exceeded the limit." : string.Join(" ", issues))
                : "Live page HTML and available CSS captured.";
            if (description.Length > ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters)
                description = description[..ProjectTaskNoteImage.MaximumPageCaptureStatusCharacters];
            _pending.TrySetResult(new BrowserPageContext(url, html, css, description));
        }

        public void ClearPending(string requestId)
        {
            if (_requestId != requestId) return;
            _requestId = null;
            _expectedTitle = null;
            _expectedBounds = null;
            _pending = null;
        }

        public void Disconnect()
        {
            _pending?.TrySetResult(Unavailable("Browser source connection closed before capture finished."));
            lock (_siteGate) _sitePending?.TrySetResult(new SiteResponse("unavailable", 0, -1));
        }

        private static bool MatchesWindowBounds(JsonElement result, BrowserWindowBounds expected)
        {
            if (!result.TryGetProperty("windowBounds", out var bounds) ||
                bounds.ValueKind != JsonValueKind.Object) return false;
            var left = ReadInt(bounds, "left");
            var top = ReadInt(bounds, "top");
            var width = ReadInt(bounds, "width");
            var height = ReadInt(bounds, "height");
            if (width <= 0 || height <= 0) return false;
            static bool Near(double a, double b, int tolerance) => Math.Abs(a - b) <= tolerance;
            static bool Similar(double left, double top, double width, double height,
                BrowserWindowBounds candidate) =>
                Near(left, candidate.Left, 36) && Near(top, candidate.Top, 36) &&
                Near(width, candidate.Width, 48) && Near(height, candidate.Height, 48);
            if (Similar(left, top, width, height, expected)) return true;
            var scale = expected.Dpi / 96.0;
            return scale > 0 && Similar(left * scale, top * scale, width * scale, height * scale, expected);
        }
    }
}
