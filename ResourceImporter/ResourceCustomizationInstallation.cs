using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Win32;
using Tecnomatix.Engineering;

namespace TxTools.ResourceImporter
{
    internal static class ResourceCustomizationInstallation
    {
        private static readonly string[] VendorKeys =
        {
            @"SOFTWARE\Tecnomatix", @"SOFTWARE\Siemens\Tecnomatix", @"SOFTWARE\TecnomatixRoot"
        };
        private static readonly string[] LocationValues = { "InstallLocation", "InstallDir", "InstallPath", "Path", "RootDir", "Root" };
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string SdkFile = "Tecnomatix.Engineering.dll";

        internal static string FindDefault()
        {
            string path = FindInInstallation(TxApplication.InstallationDirectory);
            if (path != null) return path;

            string sdk = typeof(TxApplication).Assembly.Location;
            path = FindInInstallation(Path.GetDirectoryName(sdk));
            if (path != null) return path;

            var currentVersion = FileVersionInfo.GetVersionInfo(sdk).FileVersion;
            if (string.IsNullOrWhiteSpace(currentVersion)) return null;
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string location in ReadRegistryLocations())
            {
                path = FindInInstallation(location);
                if (path == null) continue;
                string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)));
                if (new[] { root, Path.Combine(root, "bin"), Path.Combine(root, "bin64") }
                    .Select(directory => Path.Combine(directory, SdkFile)).Where(File.Exists)
                    .Any(file => string.Equals(FileVersionInfo.GetVersionInfo(file).FileVersion, currentVersion,
                        StringComparison.OrdinalIgnoreCase)))
                    matches.Add(path);
            }
            if (matches.Count > 1)
                throw new InvalidOperationException("注册表中存在多个与当前 SDK 版本相同的 PS 安装，无法确定应使用哪个 defaultCust.ppc。");
            return matches.SingleOrDefault();
        }

        internal static string FindInInstallation(string location)
        {
            if (string.IsNullOrWhiteSpace(location)) return null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
                if (!Path.IsPathRooted(path)) return null;
                path = Path.GetFullPath(path);
                if (File.Exists(path)) path = Path.GetDirectoryName(path);
                foreach (string root in new[] { path, Path.Combine(path, "eMPower"), Path.GetDirectoryName(path) })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string candidate = Path.Combine(root, "InitData", "DefaultCust", "defaultCust.ppc");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            return null;
        }

        private static IEnumerable<string> ReadRegistryLocations()
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var registry = RegistryKey.OpenBaseKey(hive, view))
                    {
                        foreach (string keyName in VendorKeys)
                        using (var vendor = registry.OpenSubKey(keyName))
                            ReadVendorLocations(vendor, locations, 3);

                        using (var uninstall = registry.OpenSubKey(UninstallKey))
                        {
                            if (uninstall == null) continue;
                            foreach (string product in uninstall.GetSubKeyNames())
                            using (var key = uninstall.OpenSubKey(product))
                            {
                                string name = key?.GetValue("DisplayName") as string;
                                if (name == null || (name.IndexOf("Tecnomatix", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    name.IndexOf("Process Simulate", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                                if (key.GetValue("InstallLocation") is string path) locations.Add(path);
                            }
                        }
                    }
                }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return locations;
        }

        private static void ReadVendorLocations(RegistryKey key, ISet<string> locations, int depth)
        {
            if (key == null) return;
            foreach (string name in LocationValues)
                if (key.GetValue(name) is string path) locations.Add(path);
            if (depth == 0) return;
            foreach (string name in key.GetSubKeyNames())
            using (var child = key.OpenSubKey(name))
                ReadVendorLocations(child, locations, depth - 1);
        }
    }
}
