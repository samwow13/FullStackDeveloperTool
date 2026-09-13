using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FullStackLauncher.Services;

/// <summary>DPAPI protection tied to the current Windows user and an application-specific purpose.</summary>
internal static class WindowsUserSecretProtection
{
    public static byte[] Protect(byte[] plaintext, string purpose) => Transform(plaintext, purpose, true);
    public static byte[] Unprotect(byte[] ciphertext, string purpose) => Transform(ciphertext, purpose, false);

    private static byte[] Transform(byte[] input, string purpose, bool protect)
    {
        var entropy = Encoding.UTF8.GetBytes(purpose);
        var inputBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
            entropyBlob = new DataBlob { Length = entropy.Length, Data = Marshal.AllocHGlobal(entropy.Length) };
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            Marshal.Copy(entropy, 0, entropyBlob.Data, entropy.Length);
            // CRYPTPROTECT_UI_FORBIDDEN only; never use CRYPTPROTECT_LOCAL_MACHINE.
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!succeeded) throw new CryptographicException();
            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ClearAndFree(inputBlob, local: false);
            ClearAndFree(entropyBlob, local: false);
            ClearAndFree(outputBlob, local: true);
        }
    }

    private static void ClearAndFree(DataBlob blob, bool local)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        if (local) LocalFree(blob.Data);
        else Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
