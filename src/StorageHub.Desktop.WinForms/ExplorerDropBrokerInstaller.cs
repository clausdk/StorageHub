using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace StorageHub.Desktop;

internal static class ExplorerDropBrokerInstaller
{
    internal const string ClassId = "{D7AE012A-EC7C-4CC3-AD34-7EE7155518CE}";
    internal const string FileName = "StorageHub.ShellExtension.Native.dll";

    public static bool EnsureRegistered(string applicationDirectory)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(applicationDirectory, FileName));
        if (!File.Exists(sourcePath)) return false;
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath)))[..16];
            var brokerDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StorageHub", "ShellExtensions", fingerprint);
            Directory.CreateDirectory(brokerDirectory);
            var dllPath = Path.Combine(brokerDirectory, FileName);
            if (!File.Exists(dllPath)) File.Copy(sourcePath, dllPath, overwrite: false);
            using (var server = Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\CLSID\{ClassId}\InprocServer32", writable: true))
            {
                if (server is null) throw new UnauthorizedAccessException("The broker COM registration key could not be opened.");
                server.SetValue(null, dllPath, RegistryValueKind.String);
                server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
            }
            using (var handler = Registry.CurrentUser.CreateSubKey(
                       @"Software\Classes\Directory\shellex\CopyHookHandlers\StorageHub", writable: true))
            {
                if (handler is null) throw new UnauthorizedAccessException("The Explorer copy-hook registration key could not be opened.");
                handler.SetValue(null, ClassId, RegistryValueKind.String);
            }
            SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED / SHCNF_IDLIST
            DeleteObsoleteBrokerDirectories(Path.GetDirectoryName(brokerDirectory)!, brokerDirectory);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (CryptographicException) { return false; }
        catch (SecurityException) { return false; }
    }

    public static bool Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\Directory\shellex\CopyHookHandlers\StorageHub",
                throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"Software\Classes\CLSID\{ClassId}",
                throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED / SHCNF_IDLIST

            var brokerRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StorageHub", "ShellExtensions");
            DeleteObsoleteBrokerDirectories(brokerRoot, currentDirectory: null);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (SecurityException) { return false; }
    }

    private static void DeleteObsoleteBrokerDirectories(string brokerRoot, string? currentDirectory)
    {
        if (!Directory.Exists(brokerRoot)) return;
        var root = Path.GetFullPath(brokerRoot);
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            try
            {
                var fullCandidate = Path.GetFullPath(candidate);
                if (string.Equals(fullCandidate, currentDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                var parent = Directory.GetParent(fullCandidate)?.FullName;
                if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new DirectoryInfo(fullCandidate);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Name.Length != 16 ||
                    info.Name.Any(character => !Uri.IsHexDigit(character))) continue;
                var entries = info.EnumerateFileSystemInfos().ToArray();
                if (entries.Any(entry =>
                        entry is not FileInfo ||
                        !string.Equals(entry.Name, FileName, StringComparison.OrdinalIgnoreCase) ||
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0)) continue;
                foreach (var entry in entries)
                {
                    entry.Delete();
                }
                info.Delete(recursive: false);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
