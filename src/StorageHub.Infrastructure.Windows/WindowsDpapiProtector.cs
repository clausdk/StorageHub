using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StorageHub.Security;

namespace StorageHub.Infrastructure.Windows;

public sealed partial class WindowsDpapiProtector : ISecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;

    public string Scheme => "windows-dpapi-current-user-v1";

    public unsafe byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
    {
        EnsureWindows();

        fixed (byte* plaintextPointer = plaintext)
        fixed (byte* entropyPointer = entropy)
        {
            var input = new DataBlob(plaintext.Length, plaintextPointer);
            var optionalEntropy = new DataBlob(entropy.Length, entropyPointer);
            var output = default(DataBlob);
            try
            {
                var succeeded = CryptProtectData(
                    &input,
                    0,
                    entropy.IsEmpty ? null : &optionalEntropy,
                    0,
                    0,
                    CryptProtectUiForbidden,
                    &output);
                if (succeeded == 0)
                {
                    throw CreateDpapiException("protect");
                }

                return CopyOutput(output);
            }
            finally
            {
                ReleaseOutput(output);
            }
        }
    }

    public unsafe byte[] Unprotect(ReadOnlySpan<byte> protectedData, ReadOnlySpan<byte> entropy)
    {
        EnsureWindows();

        fixed (byte* protectedDataPointer = protectedData)
        fixed (byte* entropyPointer = entropy)
        {
            var input = new DataBlob(protectedData.Length, protectedDataPointer);
            var optionalEntropy = new DataBlob(entropy.Length, entropyPointer);
            var output = default(DataBlob);
            nint description = 0;
            try
            {
                var succeeded = CryptUnprotectData(
                    &input,
                    &description,
                    entropy.IsEmpty ? null : &optionalEntropy,
                    0,
                    0,
                    CryptProtectUiForbidden,
                    &output);
                if (succeeded == 0)
                {
                    throw CreateDpapiException("unprotect");
                }

                return CopyOutput(output);
            }
            finally
            {
                ReleaseOutput(output);
                if (description != 0)
                {
                    _ = LocalFree(description);
                }
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI is only available on Windows.");
        }
    }

    private static CryptographicException CreateDpapiException(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new CryptographicException($"Windows DPAPI could not {operation} the payload (Win32 error {error}).");
    }

    private static unsafe byte[] CopyOutput(DataBlob output)
    {
        if (output.Size < 0 || (output.Size > 0 && output.Data is null))
        {
            throw new CryptographicException("Windows DPAPI returned an invalid payload.");
        }

        var result = new byte[output.Size];
        if (output.Size > 0)
        {
            new ReadOnlySpan<byte>(output.Data, output.Size).CopyTo(result);
        }

        return result;
    }

    private static unsafe void ReleaseOutput(DataBlob output)
    {
        if (output.Data is null)
        {
            return;
        }

        if (output.Size > 0)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>(output.Data, output.Size));
        }

        _ = LocalFree((nint)output.Data);
    }

    [LibraryImport("Crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int CryptProtectData(
        DataBlob* dataIn,
        nint dataDescription,
        DataBlob* optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        DataBlob* dataOut);

    [LibraryImport("Crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int CryptUnprotectData(
        DataBlob* dataIn,
        nint* dataDescription,
        DataBlob* optionalEntropy,
        nint reserved,
        nint prompt,
        uint flags,
        DataBlob* dataOut);

    [LibraryImport("Kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LocalFree(nint memory);

    [StructLayout(LayoutKind.Sequential)]
    private readonly unsafe struct DataBlob(int size, byte* data)
    {
        public readonly int Size = size;
        public readonly byte* Data = data;
    }
}
