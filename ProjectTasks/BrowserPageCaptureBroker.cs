using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security;
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

/// <summary>A value-free result of an explicit website shortcut login fill.</summary>
internal sealed record BrowserLoginFillResult(bool Filled, string Status);

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
    /// Opens a website shortcut and fills its main-frame login form without invoking submission.
    /// Credentials are sent to exactly one authenticated browser only after its preflight.
    /// An explicit browser limits selection to that browser's paired profiles.
    /// </summary>
    public static async Task<BrowserLoginFillResult> FillWebsiteLoginAsync(
        string url, string profileId, string? usernameSelector, string? passwordSelector,
        CancellationToken cancellationToken, string? browser = null)
    {
        if (browser is not (null or "chrome" or "msedge"))
            return LoginUnavailable("Saved website browser is unsupported. Choose Chrome, Edge, or Automatic in tool configuration.");
        if (!TryLoginOrigin(url, out var origin) || string.IsNullOrWhiteSpace(profileId) ||
            (usernameSelector?.Length ?? 0) > 1024 || (passwordSelector?.Length ?? 0) > 1024)
            return LoginUnavailable("Website login requires a valid HTTPS address (or loopback HTTP) and saved credentials.");
        try { GetSecret(); EnsureListening(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { return LoginUnavailable("Browser pairing could not be read. Review browser extension setup."); }
        if (_startupError != null)
            return LoginUnavailable("Browser connection could not start. Review browser extension setup.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var acquired = false;
        BrowserConnection? connection = null;
        var requestId = Guid.NewGuid().ToString("N");
        var selectedName = browser == "chrome" ? "Chrome" : "Edge";
        bool MatchesSelectedBrowser(BrowserConnection item) => item.IsOpen && item.SupportsLoginFilling &&
            (browser == null || item.Browser == browser);
        try
        {
            await CaptureGate.WaitAsync(timeout.Token);
            acquired = true;
            var deadline = DateTime.UtcNow.AddSeconds(browser == null ? 3 : 10);
            var attemptedBrowserStart = false;
            do
            {
                lock (Gate)
                {
                    var capable = Connections.Where(MatchesSelectedBrowser).ToArray();
                    if (capable.Length > 1)
                        return LoginUnavailable(browser == null
                            ? "More than one paired browser profile can fill logins. Close extra profiles and retry."
                            : $"More than one paired {selectedName} profile can fill logins. Close extra {selectedName} profiles and retry.");
                    connection = capable.SingleOrDefault();
                }
                if (connection != null) break;
                if (browser != null && !attemptedBrowserStart)
                {
                    attemptedBrowserStart = true;
                    try
                    {
                        // Start only the selected browser, with no URL or credentials.
                        // Its authenticated extension owns choosing/opening the login tab.
                        await Task.Run(() => WebsiteBrowserLauncher.Start(browser), timeout.Token);
                        deadline = DateTime.UtcNow.AddSeconds(10);
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                        IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        return LoginUnavailable($"{selectedName} could not be started. Check its installation and browser selection, then retry.");
                    }
                }
                await Task.Delay(100, timeout.Token);
            } while (DateTime.UtcNow < deadline);
            if (connection == null)
                return LoginUnavailable(browser == null
                    ? "Install, reload and pair the browser extension in one Chrome or Edge profile, then retry."
                    : $"{selectedName} did not connect. Open {selectedName}, then install, reload and pair the extension in one {selectedName} profile and retry.");

            // Read only the user-selected profile. Never enumerate credentials or send them
            // in inventory, capture, page context, status, settings, or tab URLs.
            using var credentials = ToolCredentialStore.Read(profileId);
            if (credentials.Profile.Kind != "Website" ||
                !TryLoginOrigin(credentials.Profile.Origin, out var savedOrigin) || savedOrigin != origin)
                return LoginUnavailable("Saved website credentials do not match this website origin. Edit the shortcut or credential profile.");
            if (credentials.Profile.UserName.Length is < 1 or > 1024 || credentials.Password.Length is < 1 or > 4096)
                return LoginUnavailable("Saved website credentials are unavailable or exceed the supported field limits.");

            var ready = await connection.PreflightLoginAsync(requestId, url, origin,
                usernameSelector, passwordSelector, timeout.Token);
            if (ready.State != "ready") return LoginResult(ready.State);
            lock (Gate)
            {
                if (!MatchesSelectedBrowser(connection) || Connections.Count(MatchesSelectedBrowser) != 1 ||
                    !Connections.Contains(connection))
                    return LoginUnavailable(browser == null
                        ? "Browser profiles changed before login filling. Close extra profiles and retry."
                        : $"{selectedName} profiles changed before login filling. Close extra {selectedName} profiles and retry.");
            }
            var result = await connection.FillLoginAsync(requestId, credentials.Profile.UserName,
                credentials.Password, timeout.Token);
            return LoginResult(result.State);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return LoginUnavailable("Website login timed out. Open its login page and retry."); }
        catch (Exception error) when (error is TimeoutException or IOException or WebSocketException or
            InvalidOperationException or CryptographicException or UnauthorizedAccessException or ArgumentException or ToolCredentialStoreException)
        { return LoginUnavailable("Website login could not read credentials or complete the browser request. Review configuration and retry."); }
        finally
        {
            if (connection != null)
            {
                using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try { await connection.SendAsync(new { type = "cancelLoginFill", requestId }, cancelTimeout.Token); }
                catch (Exception error) when (error is OperationCanceledException or IOException or WebSocketException or InvalidOperationException) { }
            }
            if (acquired) CaptureGate.Release();
        }
    }

    private static BrowserLoginFillResult LoginUnavailable(string status) => new(false, status);
    private static BrowserLoginFillResult LoginResult(string state) => state switch
    {
        "filled" => new(true, "Website login fields filled."),
        "selectors-needed" => LoginUnavailable("Login fields are missing or ambiguous. Configure username and password CSS selectors, then retry."),
        "unsafe-form" => LoginUnavailable("Login form has an unsafe or cross-origin submission destination. Password was not filled."),
        "origin-changed" => LoginUnavailable("Website redirected or changed origin. Save credentials for its exact login origin before retrying."),
        "tab-ambiguous" => LoginUnavailable("Several tabs match this website. Close extra matching tabs and retry."),
        "expired" => LoginUnavailable("Website login preparation expired. Open its login page and retry."),
        _ => LoginUnavailable("Website login could not access a ready main-frame form. Check extension site access and retry.")
    };

    private static bool TryLoginOrigin(string? value, out string origin) =>
        ToolCredentialStore.TryWebsiteOrigin(value, out origin);

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
                    ReadInt(hello.RootElement, "siteOpenVersion") == 1,
                    ReadInt(hello.RootElement, "loginFillVersion") == 1);
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
                            case "loginFillResult": connection.CompleteLogin(message.RootElement); break;
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
    private sealed record LoginResponse(string State);

    private sealed class BrowserConnection(string browser, WebSocket socket, bool supportsSiteOpening, bool supportsLoginFilling)
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
        private string? _loginRequestId;
        private string? _loginPhase;
        private TaskCompletionSource<LoginResponse>? _loginPending;

        public string Browser { get; } = browser;
        public bool IsOpen => socket.State == WebSocketState.Open;
        public bool SupportsSiteOpening { get; } = supportsSiteOpening;
        public bool SupportsLoginFilling { get; } = supportsLoginFilling;
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

        public async Task<LoginResponse> PreflightLoginAsync(string requestId, string url, string origin,
            string? usernameSelector, string? passwordSelector, CancellationToken cancellationToken)
        {
            var completion = BeginLoginRequest(requestId, "prepare");
            try
            {
                await SendAsync(new
                {
                    type = "prepareLoginFill", requestId, url, origin,
                    usernameSelector = usernameSelector?.Trim() ?? "",
                    passwordSelector = passwordSelector?.Trim() ?? "",
                    notAfterUtc = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeMilliseconds()
                }, cancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(21), cancellationToken);
            }
            finally { ClearLoginRequest(requestId, completion); }
        }

        public async Task<LoginResponse> FillLoginAsync(string requestId, string username,
            SecureString password, CancellationToken cancellationToken)
        {
            var completion = BeginLoginRequest(requestId, "fill");
            try
            {
                await _sendGate.WaitAsync(cancellationToken);
                try
                {
                    // Serialize directly from the temporary unmanaged SecureString buffer.
                    // Never create a managed password string; erase all serialized bytes.
                    // Capacity covers the enforced limits even with six-byte JSON escapes,
                    // preventing resize copies and a stream writer's separate pooled buffer.
                    var output = new ArrayBufferWriter<byte>(40_000);
                    try
                    {
                        using (var writer = new Utf8JsonWriter(output))
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "fillPreparedLogin");
                            writer.WriteString("requestId", requestId);
                            writer.WriteString("username", username);
                            WriteSecurePassword(writer, password);
                            writer.WriteNumber("notAfterUtc", DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds());
                            writer.WriteEndObject();
                        }
                        await socket.SendAsync(output.WrittenMemory,
                            WebSocketMessageType.Text, true, cancellationToken);
                    }
                    finally
                    {
                        if (MemoryMarshal.TryGetArray(output.WrittenMemory, out var bytes) && bytes.Array != null)
                            CryptographicOperations.ZeroMemory(bytes.Array);
                    }
                }
                finally { _sendGate.Release(); }
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(6), cancellationToken);
            }
            finally { ClearLoginRequest(requestId, completion); }
        }

        private static unsafe void WriteSecurePassword(Utf8JsonWriter writer, SecureString password)
        {
            var pointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            byte[]? encoded = null;
            try
            {
                // Escape every UTF-16 code unit into an owned, bounded JSON string.
                // Utf8JsonWriter's string APIs may return secret escape buffers to an
                // uncleared shared pool; raw UTF-8 avoids that separate escape path.
                var characters = new ReadOnlySpan<char>((void*)pointer, password.Length);
                encoded = new byte[checked(characters.Length * 6 + 2)];
                const string hexadecimal = "0123456789abcdef";
                var index = 0;
                encoded[index++] = (byte)'"';
                foreach (var character in characters)
                {
                    encoded[index++] = (byte)'\\';
                    encoded[index++] = (byte)'u';
                    encoded[index++] = (byte)hexadecimal[(character >> 12) & 15];
                    encoded[index++] = (byte)hexadecimal[(character >> 8) & 15];
                    encoded[index++] = (byte)hexadecimal[(character >> 4) & 15];
                    encoded[index++] = (byte)hexadecimal[character & 15];
                }
                encoded[index] = (byte)'"';
                // Quotes, backslashes, controls and surrogate code units all follow
                // the same explicit \uXXXX encoding; no input validation or parsing
                // is needed for these exclusively generated JSON string bytes.
                writer.WritePropertyName("password");
                writer.WriteRawValue(encoded, skipInputValidation: true);
            }
            finally
            {
                if (encoded != null) CryptographicOperations.ZeroMemory(encoded);
                Marshal.ZeroFreeGlobalAllocUnicode(pointer);
            }
        }

        private TaskCompletionSource<LoginResponse> BeginLoginRequest(string requestId, string phase)
        {
            var completion = new TaskCompletionSource<LoginResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_siteGate) { _loginRequestId = requestId; _loginPhase = phase; _loginPending = completion; }
            return completion;
        }

        private void ClearLoginRequest(string requestId, TaskCompletionSource<LoginResponse> completion)
        {
            lock (_siteGate)
                if (_loginRequestId == requestId && _loginPending == completion)
                { _loginRequestId = null; _loginPhase = null; _loginPending = null; }
        }

        public void CompleteLogin(JsonElement result)
        {
            var state = ReadString(result, "status");
            if (state is not ("ready" or "filled" or "selectors-needed" or "unsafe-form" or
                "origin-changed" or "tab-ambiguous" or "expired")) state = "unavailable";
            lock (_siteGate)
                if (_loginPending != null && ReadString(result, "requestId") == _loginRequestId &&
                    ReadString(result, "phase") == _loginPhase)
                    _loginPending.TrySetResult(new LoginResponse(state));
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
            lock (_siteGate) _loginPending?.TrySetResult(new LoginResponse("unavailable"));
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
