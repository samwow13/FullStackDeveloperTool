using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullStackLauncher.Models;

namespace FullStackLauncher.Services;

/// <summary>
/// Bounded read-only WinRM actions using explicit credentials and normal Windows trust.
/// This service never changes WinRM, firewall, TrustedHosts, TLS trust, or remote machine state.
/// Native desktop and folder clients own their separate Windows authentication prompts.
/// </summary>
public sealed class LanRemoteControlService
{
    private const string CredentialPurpose = "FullStackLauncher.LanRemoteControl.v1";
    private const int OutputLimit = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<LanRemoteSession> ConnectAsync(string host, bool useHttps,
        LanRemoteCredentials credentials, CancellationToken cancellationToken = default, string? expectedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var target = NormalizeTarget(host);
        ValidateTransport(target, useHttps);
        var address = await ResolveLanTargetAsync(target, cancellationToken).ConfigureAwait(false);
        if (expectedAddress is not null && !address.Equals(NormalizeTarget(expectedAddress), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connection name resolves to a different PC. Use a name for the selected LAN address.");
        var owned = credentials.Copy();
        try
        {
            var response = await ExecuteAsync(target, useHttps, owned, LanRemoteAction.SystemInfo, cancellationToken).ConfigureAwait(false);
            var identity = ReadIdentity(response);
            return new(target, address, useHttps, owned, identity, DateTimeOffset.UtcNow);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    public async Task<LanRemoteCommandResult> QueryAsync(LanRemoteSession session,
        LanRemoteAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (action is not (LanRemoteAction.SystemInfo or LanRemoteAction.Processes))
            throw new ArgumentOutOfRangeException(nameof(action));
        using var credentials = session.CopyCredentials();
        var address = await ResolveLanTargetAsync(session.Target, cancellationToken).ConfigureAwait(false);
        if (!address.Equals(session.Address, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target's LAN address changed. Sign out, rediscover it, and sign in again.");
        var response = await ExecuteAsync(session.Target, session.UseHttps, credentials, action, cancellationToken).ConfigureAwait(false);
        var identity = ReadIdentity(response);
        if (!identity.ComputerName.Equals(session.Identity.ComputerName, StringComparison.OrdinalIgnoreCase)
            || !identity.AccountSid.Equals(session.Identity.AccountSid, StringComparison.Ordinal))
            throw new InvalidOperationException("The authenticated computer or account changed. Sign out and identify the target again.");
        var text = action == LanRemoteAction.SystemInfo ? FormatSystem(response) : FormatProcesses(response);
        return new(action == LanRemoteAction.SystemInfo ? "System information" : "Processes", text, DateTimeOffset.UtcNow);
    }

    public static string NormalizeTarget(string host)
    {
        var target = (host ?? "").Trim().TrimEnd('.');
        if (target.Length is < 1 or > 253 || target.Any(char.IsControl))
            throw new ArgumentException("Enter a LAN computer name or IPv4 address.");
        if (IPAddress.TryParse(target, out var address))
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Choose a discovered IPv4 LAN address. IPv6 is not supported here.");
            return address.ToString();
        }
        foreach (var label in target.Split('.'))
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-'
                || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                throw new ArgumentException("Enter a computer name or IPv4 address without a scheme, path, or port.");
        return target;
    }

    public static async Task OpenRemoteDesktopAsync(string host, CancellationToken cancellationToken = default)
    {
        var target = NormalizeTarget(host);
        var address = await ResolveLanTargetAsync(target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Pin the reviewed LAN address. Windows displays its own certificate/account prompt.
        OpenNative(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), "/v:" + address, "/prompt");
    }

    public static async Task OpenSharedFoldersAsync(string host, CancellationToken cancellationToken = default)
    {
        var target = NormalizeTarget(host);
        var address = await ResolveLanTargetAsync(target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // No SMB connection or credentials are created by the launcher; Explorer owns sign-in.
        OpenNative(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), @"\\" + address);
    }

    private static void OpenNative(string executable, params string[] arguments)
    {
        if (!File.Exists(executable)) throw new InvalidOperationException("The required Windows client is not installed on this computer.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not open the client.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Windows could not open the client. Check that Remote Desktop or File Explorer is installed.");
        }
    }

    private static void ValidateTransport(string target, bool useHttps)
    {
        if (!useHttps && IPAddress.TryParse(target, out _))
            throw new ArgumentException("HTTP requires a domain computer name with Kerberos. Use HTTPS for an IP address or workgroup PC.");
    }

    private static async Task<string> ResolveLanTargetAsync(string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IPAddress[] addresses;
        if (IPAddress.TryParse(target, out var literal)) addresses = [literal];
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try { addresses = await Dns.GetHostAddressesAsync(target, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new InvalidOperationException("Computer name lookup timed out. Choose its discovered IPv4 LAN address."); }
            catch (SocketException)
            { throw new InvalidOperationException("Computer name could not be resolved. Choose its discovered IPv4 LAN address."); }
        }
        var unique = addresses.Select(a => a.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length != 1)
            throw new InvalidOperationException("Computer name must resolve to one IPv4 LAN address. Choose its discovered address instead.");
        if (!LanDiscoveryService.IsAllowedLanAddress(unique[0]) || LanDiscoveryService.IsLocalAddress(unique[0]))
            throw new InvalidOperationException("Choose another PC on a directly connected private IPv4 LAN. Local and outside-LAN targets are blocked.");
        return unique[0];
    }

    private static async Task<Response> ExecuteAsync(string target, bool useHttps, LanRemoteCredentials credentials,
        LanRemoteAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("Windows PowerShell is required for WinRM management.");
        var encryptedPassword = ProtectPassword(credentials);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Target = target, UseHttps = useHttps, UserName = credentials.UserName,
            Credential = Convert.ToBase64String(encryptedPassword), Action = action.ToString()
        });
        CryptographicOperations.ZeroMemory(encryptedPassword);
        using var process = new Process();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(FixedScript)) })
            start.ArgumentList.Add(argument);
        process.StartInfo = start;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        Task<(string Text, bool Truncated)>? output = null;
        Task<(string Text, bool Truncated)>? error = null;
        var started = false;
        try
        {
            try { started = process.Start(); }
            catch (Win32Exception) { throw new InvalidOperationException("Windows PowerShell could not start. Check local Windows policy."); }
            if (!started) throw new InvalidOperationException("Windows PowerShell could not start.");
            output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var stdout = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (stdout.Truncated || stderr.Truncated)
                throw new InvalidOperationException("Remote output exceeded the management limit. Try the smaller System information action.");
            Response? response;
            try { response = JsonSerializer.Deserialize<Response>(stdout.Text.Trim(), JsonOptions); }
            catch (JsonException) { throw new InvalidOperationException("WinRM returned an unreadable response. Check Windows PowerShell and remote policy."); }
            if (response is null || !response.Success || process.ExitCode != 0)
                throw new InvalidOperationException(DescribeError(response?.ErrorCode, useHttps));
            return response;
        }
        catch (OperationCanceledException)
        {
            var exited = !started || await StopOwnedProcessAsync(process).ConfigureAwait(false);
            if (token.IsCancellationRequested && exited) throw;
            throw new InvalidOperationException((token.IsCancellationRequested ? "Remote read canceled." : "WinRM read timed out. Check the selected PC and its WinRM listener.")
                + (exited ? "" : " The launcher's PowerShell process exit was not confirmed; wait before signing in again."));
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Communication with Windows PowerShell failed. Try signing in again after checking local Windows policy.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (started && !process.HasExited) await StopOwnedProcessAsync(process).ConfigureAwait(false);
            if (output is not null && error is not null)
            {
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException) { }
            }
        }
    }

    private static byte[] ProtectPassword(LanRemoteCredentials credentials)
    {
        using var password = credentials.CopyPassword();
        var memory = Marshal.SecureStringToGlobalAllocUnicode(password);
        var plaintext = new byte[password.Length * 2];
        try
        {
            Marshal.Copy(memory, plaintext, 0, plaintext.Length);
            return WindowsUserSecretProtection.Protect(plaintext, CredentialPurpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            Marshal.ZeroFreeGlobalAllocUnicode(memory);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            var keep = Math.Min(count, OutputLimit - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            if (keep < count) truncated = true;
        }
        return (text.ToString(), truncated);
    }

    private static async Task<bool> StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or TimeoutException) { return false; }
    }

    private static LanRemoteIdentity ReadIdentity(Response response)
    {
        if (string.IsNullOrWhiteSpace(response.ComputerName) || string.IsNullOrWhiteSpace(response.Identity)
            || string.IsNullOrWhiteSpace(response.AccountSid) || string.IsNullOrWhiteSpace(response.OperatingSystem))
            throw new InvalidOperationException("WinRM authenticated, but the remote Windows identity could not be verified.");
        return new(response.ComputerName, response.OperatingSystem, response.Identity, response.AccountSid);
    }

    private static string FormatSystem(Response response) =>
        $"Computer: {response.ComputerName}\nSigned in as: {response.Identity}\nWindows: {response.OperatingSystem}\nVersion: {response.Version}\n"
        + $"Manufacturer: {response.Manufacturer}\nModel: {response.Model}\nMemory: {response.MemoryGb.ToString("0.0", CultureInfo.InvariantCulture)} GB\nLast boot: {response.LastBoot}";

    private static string FormatProcesses(Response response)
    {
        var processes = response.Processes ?? [];
        var text = new StringBuilder($"{response.ComputerName} · {response.Identity}\nLargest 150 processes by working set; command lines are excluded.\n\n");
        text.AppendLine("     PID     Memory MB   Process");
        foreach (var item in processes.Take(150))
            text.AppendLine($"{item.ProcessId,8} {item.MemoryMb.ToString("0.0", CultureInfo.InvariantCulture),13}   {item.Name}");
        return text.ToString().TrimEnd();
    }

    private static string DescribeError(string? code, bool https) => code switch
    {
        "AccessDenied" => "WinRM sign-in denied. Check the account password (not a Windows Hello PIN), PCNAME\\user or DOMAIN\\user, and remote management permission.",
        "Certificate" => "WinRM HTTPS certificate validation failed. Use the certificate's computer name and a certificate trusted by Windows; certificate checks cannot be bypassed.",
        "Kerberos" => "Kerberos authentication failed. HTTP requires a domain computer name and configured domain trust. Use HTTPS for workgroup PCs.",
        "Credential" => "Windows could not safely transfer the credential to its local PowerShell process. Re-enter the account and password.",
        "Query" => "Authenticated remote read failed. Check that this account can read Windows system information and processes through WinRM.",
        _ => https
            ? "WinRM HTTPS connection failed. The selected PC needs an existing HTTPS listener on port 5986, a trusted matching certificate, and remote management permission. Use its certificate's computer name."
            : "WinRM connection failed. The selected domain PC needs an existing listener on port 5985, Kerberos domain trust, and remote management permission."
    };

    private sealed class Response
    {
        public Response() { }
        public bool Success { get; set; }
        public string? ErrorCode { get; set; }
        public string ComputerName { get; set; } = "";
        public string Identity { get; set; } = "";
        public string AccountSid { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public string Version { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public double MemoryGb { get; set; }
        public string LastBoot { get; set; } = "";
        public ProcessRow[]? Processes { get; set; }
    }

    private sealed class ProcessRow
    {
        public ProcessRow() { }
        public string Name { get; set; } = "";
        public int ProcessId { get; set; }
        public double MemoryMb { get; set; }
    }

    // Only this constant script is executed. User data enters through DPAPI-protected stdin,
    // never through script interpolation, process arguments, environment variables, or files.
    private const string FixedScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $session = $null
        $password = $null
        $credential = $null
        $bytes = $null
        $stage = 'Credential'
        try {
            Add-Type -AssemblyName System.Security
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
                [Convert]::FromBase64String([string]$request.Credential),
                [Text.Encoding]::UTF8.GetBytes('FullStackLauncher.LanRemoteControl.v1'),
                [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
            $password = [System.Security.SecureString]::new()
            for ($i = 0; $i -lt $bytes.Length; $i += 2) {
                $password.AppendChar([char]([int]$bytes[$i] -bor ([int]$bytes[$i + 1] -shl 8)))
            }
            [Array]::Clear($bytes, 0, $bytes.Length)
            $bytes = $null
            $password.MakeReadOnly()
            $credential = [System.Management.Automation.PSCredential]::new([string]$request.UserName, $password)
            if ($request.Action -notin @('SystemInfo', 'Processes')) { throw 'Unsupported action' }
            $options = New-PSSessionOption -OpenTimeout 12000 -OperationTimeout 15000 -CancelTimeout 3000 -IdleTimeout 60000 -MaximumConnectionRedirectionCount 0 -MaximumReceivedDataSizePerCommand 2097152 -MaximumReceivedObjectSize 1048576
            $parameters = @{
                ComputerName = [string]$request.Target
                Name = 'FullStackLauncher-LAN'
                Credential = $credential
                ConfigurationName = 'Microsoft.PowerShell'
                SessionOption = $options
                ErrorAction = 'Stop'
            }
            if ([bool]$request.UseHttps) {
                $parameters.UseSSL = $true
                $parameters.Authentication = 'Negotiate'
            } else {
                $parameters.Authentication = 'Kerberos'
            }
            $stage = 'Connect'
            $session = New-PSSession @parameters
            $stage = 'Query'
            $system = Invoke-Command -Session $session -ScriptBlock {
                $ErrorActionPreference = 'Stop'
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                try {
                    $os = Get-CimInstance -ClassName Win32_OperatingSystem -OperationTimeoutSec 10
                    $computer = Get-CimInstance -ClassName Win32_ComputerSystem -OperationTimeoutSec 10
                    [pscustomobject]@{
                        ComputerName = [string]$env:COMPUTERNAME
                        Identity = [string]$identity.Name
                        AccountSid = [string]$identity.User.Value
                        OperatingSystem = [string]$os.Caption
                        Version = [string]$os.Version
                        Manufacturer = [string]$computer.Manufacturer
                        Model = [string]$computer.Model
                        MemoryGb = [Math]::Round(([double]$computer.TotalPhysicalMemory / 1GB), 1)
                        LastBoot = $os.LastBootUpTime.ToString('o')
                    }
                } finally { $identity.Dispose() }
            }
            $processes = @()
            if ($request.Action -eq 'Processes') {
                $processes = @(Invoke-Command -Session $session -ScriptBlock {
                    Get-CimInstance -ClassName Win32_Process -OperationTimeoutSec 10 |
                        Sort-Object -Property WorkingSetSize -Descending | Select-Object -First 150 |
                        ForEach-Object {
                            [pscustomobject]@{
                                Name = [string]$_.Name
                                ProcessId = [int]$_.ProcessId
                                MemoryMb = [Math]::Round(([double]$_.WorkingSetSize / 1MB), 1)
                            }
                        }
                })
            }
            $result = @{
                Success = $true
                ComputerName = $system.ComputerName
                Identity = $system.Identity
                AccountSid = $system.AccountSid
                OperatingSystem = $system.OperatingSystem
                Version = $system.Version
                Manufacturer = $system.Manufacturer
                Model = $system.Model
                MemoryGb = $system.MemoryGb
                LastBoot = $system.LastBoot
                Processes = $processes
            }
            [Console]::Out.WriteLine(($result | ConvertTo-Json -Depth 4 -Compress))
        } catch {
            # Raw exception text can contain server-controlled data. Publish fixed categories only.
            $detail = [string]$_.Exception.Message + ' ' + [string]$_.FullyQualifiedErrorId
            $code = if ($stage -eq 'Credential') { 'Credential' }
                elseif ($detail -match 'AccessDenied|0x80070005|0x8007052e|Access is denied|logon failure|user name or password|credentials were rejected') { 'AccessDenied' }
                elseif ($detail -match 'certificate|SSL|0x80072f8f') { 'Certificate' }
                elseif ($detail -match 'Kerberos|ServerNotTrusted|0x8009030e|0x80090322') { 'Kerberos' }
                elseif ($stage -eq 'Query') { 'Query' }
                else { 'Connect' }
            [Console]::Out.WriteLine((@{ Success = $false; ErrorCode = $code } | ConvertTo-Json -Compress))
            exit 1
        } finally {
            if ($session) { Remove-PSSession -Session $session -ErrorAction SilentlyContinue }
            if ($bytes) { [Array]::Clear($bytes, 0, $bytes.Length) }
            if ($password) { $password.Dispose() }
            $credential = $null
            $request = $null
        }
        """;
}
