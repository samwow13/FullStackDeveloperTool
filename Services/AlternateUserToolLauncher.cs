using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FullStackLauncher.Services;

/// <summary>
/// Starts a new desktop process with an explicitly selected Windows account. This never focuses
/// an existing process and does not elevate through UAC or pass passwords through commands.
/// </summary>
public static class AlternateUserToolLauncher
{
    private static readonly HashSet<string> CommandShells = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "wsl.exe" };

    public static void Open(string executable, ToolCredentialValue credential, bool networkOnly = false, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!OperatingSystem.IsWindows())
            throw new ToolCredentialStoreException("Running as another Windows user is available only on Windows.");
        if (!credential.Profile.Kind.Equals("Windows", StringComparison.Ordinal))
            throw new ToolCredentialStoreException("Choose Windows account credentials to run this desktop app.");
        if (credential.Password.Length is < 1 or > 1280)
            throw new ToolCredentialStoreException("The saved Windows account password is invalid. Edit the credential before opening this app.");
        executable = ValidateExecutable(executable);
        if (folder is not null)
        {
            if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(executable).Equals("node.exe", StringComparison.OrdinalIgnoreCase))
                throw new ToolCredentialStoreException("Choose the editor's executable directly, rather than a service runtime.");
            folder = ValidateFolder(folder);
        }
        var (username, domain) = ParseUsername(credential.Profile.UserName);
        // Keep lpApplicationName explicit. Windows otherwise ambiguously parses paths with spaces.
        var command = new StringBuilder(QuoteWindowsArgument(executable));
        if (folder is not null) command.Append(' ').Append(QuoteWindowsArgument(folder));
        if (command.Length >= 1024)
            throw new ToolCredentialStoreException("The application or folder path is too long for running as another Windows user.");
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var process = new ProcessInformation();
        var password = IntPtr.Zero;
        try
        {
            password = Marshal.SecureStringToGlobalAllocUnicode(credential.Password);
            for (var offset = 0; offset < credential.Password.Length * 2; offset += 2)
                if (Marshal.ReadInt16(password, offset) == 0)
                    throw new ToolCredentialStoreException("The saved Windows account password is invalid. Edit the credential before opening this app.");
            if (!CreateProcessWithLogon(username, domain, password, networkOnly ? 2u : 1u,
                    executable, command, 0, IntPtr.Zero, Path.GetDirectoryName(executable)!, ref startup, out process))
                throw LaunchFailure(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (password != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(password);
            if (process.Thread != IntPtr.Zero) CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) CloseHandle(process.Process);
        }
    }

    private static string ValidateExecutable(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Any(char.IsControl)
            || executable.Contains('"') || !Path.IsPathFullyQualified(executable)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || CommandShells.Contains(Path.GetFileName(executable)))
            throw new ToolCredentialStoreException("Choose the installed application's absolute .exe path without quotes or command arguments.");
        try
        {
            var resolved = Path.GetFullPath(executable);
            if (!File.Exists(resolved))
                throw new ToolCredentialStoreException("The saved application was not found. Edit the tool and choose its installed .exe file.");
            return resolved;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolCredentialStoreException("The saved application path is invalid. Choose its installed .exe file again.");
        }
    }

    private static string ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Any(char.IsControl) || folder.Contains('"')
            || !Path.IsPathFullyQualified(folder))
            throw new ToolCredentialStoreException("Choose an absolute existing service folder before opening it in an app.");
        try
        {
            var resolved = Path.GetFullPath(folder);
            if (!Directory.Exists(resolved))
                throw new ToolCredentialStoreException("The service folder was not found. Check its configured folder.");
            return resolved;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolCredentialStoreException("The service folder path is invalid. Check its configured folder.");
        }
    }

    private static string QuoteWindowsArgument(string argument)
    {
        // Escape runs of backslashes before quotes and before the closing quote. In particular,
        // a drive root ending in '\\' must still reach the child as exactly one folder argument.
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
                quoted.Append('\\', backslashes * 2 + 1).Append(character);
            else
                quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static (string Username, string? Domain) ParseUsername(string? account)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 513 || account.Any(char.IsControl))
            throw InvalidUsername();
        account = account.Trim();
        var separator = account.IndexOf('\\');
        if (separator >= 0)
        {
            if (separator == 0 || separator == account.Length - 1 || account.LastIndexOf('\\') != separator
                || account.Contains('@'))
                throw InvalidUsername();
            return (account[(separator + 1)..], account[..separator]);
        }
        var at = account.IndexOf('@');
        if (at >= 0)
        {
            if (at == 0 || at == account.Length - 1 || account.LastIndexOf('@') != at)
                throw InvalidUsername();
            return (account, null);
        }
        return (account, ".");
    }

    private static ToolCredentialStoreException InvalidUsername() => new(
        "Enter a Windows account as DOMAIN\\username, username@domain, or a local username.");

    private static ToolCredentialStoreException LaunchFailure(int code) => new(code switch
    {
        1326 => "Windows could not sign in with the saved credentials. Edit the username or password and try again. Windows error 1326.",
        1330 => "The saved Windows account password expired. Update the password before opening this app. Windows error 1330.",
        1331 => "The saved Windows account is disabled. Contact the account administrator. Windows error 1331.",
        1380 or 1385 => $"This account is not allowed to sign in locally on this computer. Contact the account administrator. Windows error {code}.",
        740 => "This app requires UAC elevation. Running as another Windows user does not provide elevation. Use Windows' approved administrator launch flow. Windows error 740.",
        5 => "Windows denied access to the app, its working folder, or the interactive desktop for this account. Check account permissions. Windows error 5.",
        2 or 3 => $"Windows could not find the app or its working folder for this account. Choose an accessible installed .exe file. Windows error {code}.",
        1058 => "The Windows Secondary Logon service is disabled. Ask the computer administrator to enable the approved Run as different user flow. Windows error 1058.",
        1909 => "The saved Windows account is locked out. Contact the account administrator before retrying. Windows error 1909.",
        _ => $"Windows could not open the app with the saved account. Check account and computer access policies. Windows error {code}."
    });

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort ReservedSize;
        public IntPtr ReservedBytes;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithLogon(string username, string? domain, IntPtr password,
        uint logonFlags, string applicationName, StringBuilder commandLine, uint creationFlags,
        IntPtr environment, string workingDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
