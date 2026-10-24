// FluentFlyout one-file setup.
//
// A self-contained installer/uninstaller for the FluentFlyout GitHub MSIX release.
// Built with the C# compiler that ships with Windows (.NET Framework 4.x) - no external tools.
// Language level: C# 5 only (the Windows built-in csc.exe is not a Roslyn compiler).
//
// Modes:
//   (no args)        Install: trust the embedded signing cert in LocalMachine\Root,
//                    Add-AppxPackage the embedded .msixbundle, launch the app and
//                    register a standard uninstall entry (HKLM\...\Uninstall\FluentFlyoutGitHub).
//   /uninstall       Remove: Remove-AppxPackage, delete the cert by thumbprint, remove the
//                    uninstall entry.
//
// UI: hand-drawn borderless Win11-style dialog. Follows the system light/dark theme.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WinFormsTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("FluentFlyout Sideloader Setup")]
[assembly: AssemblyDescription("Unofficial one-file installer/uninstaller/updater for the FluentFlyout GitHub MSIX release.")]
[assembly: AssemblyCompany("FluentFlyout Sideloader contributors")]
[assembly: AssemblyProduct("FluentFlyout Sideloader")]
[assembly: AssemblyCopyright("Wrapper: GPL-3.0. FluentFlyout is GPL-3.0 (github.com/unchihugo/FluentFlyout).")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyVersion("2.16.0.0")]
[assembly: AssemblyFileVersion("2.16.0.0")]

namespace FluentFlyoutSetup
{
    internal static class Entry
    {
        // Names of the resources injected by build.ps1 (csc /resource:...).
        internal const string ResCert = "FluentFlyoutSetup.cer";
        internal const string ResBundle = "FluentFlyoutSetup.msixbundle";
        internal const string ResLogo = "FluentFlyoutSetup.logo.png";

        internal const string AppName = "FluentFlyout";
        internal const string UninstallSubKeyName = "FluentFlyoutGitHub";
        internal static readonly string UninstallKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallSubKeyName;

        // Identity of the exact GitHub build, read from the embedded bundle manifest.
        // The Store build can share the same package identity, so every package
        // query also requires a non-Store signature (SignatureKind -ne 'Store').
        internal static readonly string PackageName;
        internal static readonly string PackagePublisher;
        internal static readonly string VersionText;
        internal static readonly string DisplayName;
        internal const string Publisher = "Unofficial FluentFlyout Sideloader";

        static Entry()
        {
            string name = "unchihugo.FluentFlyout";
            string publisher = null;
            string version = "2.16.0";
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResBundle))
                {
                    if (stream != null && stream.CanSeek)
                    {
                        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                        {
                            ZipArchiveEntry manifest = null;
                            foreach (ZipArchiveEntry entry in archive.Entries)
                            {
                                if (string.Equals(entry.FullName, "AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase))
                                {
                                    manifest = entry;
                                    break;
                                }
                            }
                            if (manifest != null)
                            {
                                using (StreamReader reader = new StreamReader(manifest.Open()))
                                {
                                    string xml = reader.ReadToEnd();
                                    Match identity = Regex.Match(xml, "<Identity\\b[^>]*>");
                                    if (identity.Success)
                                    {
                                        Match n = Regex.Match(identity.Value, "\\bName=\"([^\"]+)\"");
                                        Match p = Regex.Match(identity.Value, "\\bPublisher=\"([^\"]+)\"");
                                        Match v = Regex.Match(identity.Value, "\\bVersion=\"([^\"]+)\"");
                                        if (n.Success) { name = n.Groups[1].Value; }
                                        if (p.Success) { publisher = p.Groups[1].Value; }
                                        if (v.Success)
                                        {
                                            int[] nums = VersionHelper.Parse(v.Groups[1].Value);
                                            if (nums != null && nums.Length >= 3)
                                            {
                                                version = nums[0] + "." + nums[1] + "." + nums[2];
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Keep the defaults; the embedded cert still identifies the package.
            }
            if (string.IsNullOrEmpty(publisher))
            {
                try
                {
                    using (X509Certificate2 cert = new X509Certificate2(EmbeddedResource(ResCert)))
                    {
                        publisher = cert.Subject;
                    }
                }
                catch (Exception)
                {
                }
            }
            PackageName = name;
            PackagePublisher = publisher;
            VersionText = version;
            DisplayName = "FluentFlyout " + version + " (GitHub)";
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            bool wantUninstall = false;
            bool previewOnly = false;
            bool checkForUpdates = false;
            bool pretendOld = false;
            bool applyContinuation = false;
            List<string> applyPaths = new List<string>();
            if (args != null)
            {
                bool collectingApplyPaths = false;
                foreach (string raw in args)
                {
                    string a = raw.Trim(new char[] { '"', ' ' });
                    if (collectingApplyPaths && applyPaths.Count < 3)
                    {
                        applyPaths.Add(raw);
                        continue;
                    }
                    if (string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-uninstall", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "/u", StringComparison.OrdinalIgnoreCase))
                    {
                        wantUninstall = true;
                    }
                    else if (string.Equals(a, "/preview", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "-preview", StringComparison.OrdinalIgnoreCase))
                    {
                        previewOnly = true;
                    }
                    else if (string.Equals(a, "/check", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(a, "-check", StringComparison.OrdinalIgnoreCase))
                    {
                        checkForUpdates = true;
                    }
                    else if (string.Equals(a, "/pretend-old", StringComparison.OrdinalIgnoreCase))
                    {
                        pretendOld = true; // test hook: force the updater to see any release as newer
                    }
                    else if (string.Equals(a, "/apply", StringComparison.OrdinalIgnoreCase))
                    {
                        applyContinuation = true;
                        collectingApplyPaths = true;
                    }
                }
            }

            // Crisp rendering on high-DPI screens and modern control styling.
            // Must happen before any window is created.
            try { SetProcessDPIAware(); }
            catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Preview shows the UI without touching anything; never needs elevation.
            if (previewOnly)
            {
                Application.Run(new SetupForm(false, true));
                return 0;
            }

            // /check : silent update check (scheduled task + update flow continuation).
            if (checkForUpdates)
            {
                if (applyContinuation && applyPaths.Count >= 2)
                {
                    // Elevated continuation applying already-downloaded files.
                    string version = applyPaths.Count >= 3 ? applyPaths[2] : null;
                    Application.Run(new UpdateForm(applyPaths[0], applyPaths[1], version));
                    return 0;
                }
                if (pretendOld)
                {
                    // Test hook. Never let it touch a Store-only installation.
                    if (!IsAppInstalled() && StorePackageInstalled())
                    {
                        ShowStoreNotice(false);
                        return 0;
                    }
                    Entry.InstalledVersion = "0.0.1";
                }
                else if (!IsAppInstalled())
                {
                    return 0; // nothing installed, nothing to update
                }
                if (!pretendOld && UpdateState.RecentlyChecked())
                {
                    return 0; // stay rate-limit friendly
                }
                string ignoredError;
                UpdateInfo info = UpdateClient.TryCheck(out ignoredError);
                if (!pretendOld)
                {
                    UpdateState.MarkCheckTime();
                }
                if (info == null)
                {
                    return 0; // offline/rate-limited: stay quiet
                }
                string baseVersion = string.IsNullOrEmpty(Entry.InstalledVersion)
                    ? Entry.VersionText
                    : Entry.InstalledVersion;
                if (VersionHelper.Compare(info.Version, baseVersion) <= 0)
                {
                    return 0; // up to date
                }
                Application.Run(new UpdateForm(info));
                return 0;
            }

            if (!IsElevated())
            {
                if (wantUninstall)
                {
                    if (!IsAppInstalled() && StorePackageInstalled())
                    {
                        ShowStoreNotice(true);
                        return 0;
                    }
                    return RelaunchElevated("/uninstall");
                }
                // Plain double-click with the app already present: offer a choice
                // between reinstall and full uninstall (no UAC before choosing).
                if (IsAppInstalled())
                {
                    Application.Run(new ChooserForm());
                    return 0;
                }
                if (StorePackageInstalled())
                {
                    ShowStoreNotice(false);
                    return 0;
                }
                return RelaunchElevated(string.Empty);
            }

            Application.Run(new SetupForm(wantUninstall, false));
            return 0;
        }

        internal static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                if (id == null)
                {
                    return false;
                }
                WindowsPrincipal principal = new WindowsPrincipal(id);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static int RelaunchElevated(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = SelfPath;
                psi.Arguments = arguments == null ? string.Empty : arguments;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch (Exception)
            {
                MessageBox.Show(
                    "Administrator permission is required for the FluentFlyout installation.\r\n\r\nThe UAC prompt was declined, so nothing was changed on this computer.",
                    "FluentFlyout Sideloader Setup",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }
        }

        internal static string InstalledVersion;

        // Publishers that belong to this GitHub sideload (current bundle + last update).
        // Publisher of the GitHub build (the Store build may share it, so queries
        // also filter on SignatureKind).
        internal static string[] GitHubPublishers()
        {
            List<string> list = new List<string>();
            if (!string.IsNullOrEmpty(PackagePublisher))
            {
                list.Add(PackagePublisher);
            }
            try
            {
                if (File.Exists(UpdateState.LastCertPath))
                {
                    using (X509Certificate2 cert = new X509Certificate2(UpdateState.LastCertPath))
                    {
                        if (!list.Contains(cert.Subject))
                        {
                            list.Add(cert.Subject);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            if (list.Count == 0)
            {
                try
                {
                    using (X509Certificate2 cert = new X509Certificate2(EmbeddedResource(ResCert)))
                    {
                        list.Add(cert.Subject);
                    }
                }
                catch (Exception)
                {
                }
            }
            return list.ToArray();
        }

        internal static string PsQuote(string value)
        {
            return "'" + (value == null ? string.Empty : value.Replace("'", "''")) + "'";
        }

        internal static string PsArray(string[] values)
        {
            StringBuilder sb = new StringBuilder("@(");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) { sb.Append(","); }
                sb.Append(PsQuote(values[i]));
            }
            sb.Append(")");
            return sb.ToString();
        }

        // Returns true when the Developer-signed GitHub package is installed.
        // Safe to call without elevation; also records the installed version.
        internal static bool IsAppInstalled()
        {
            try
            {
                string script =
                    "$ProgressPreference = 'SilentlyContinue'; " +
                    "$pub = " + PsArray(GitHubPublishers()) + "; " +
                    "try { " +
                    "$p = Get-AppxPackage -Name '*FluentFlyout*' -ErrorAction SilentlyContinue | " +
                    "Where-Object { $pub -contains $_.Publisher -and $_.SignatureKind -ne 'Store' } | " +
                    "Sort-Object Version -Descending | Select-Object -First 1; " +
                    "if ($p) { Write-Output $p.Version.ToString(); exit 1; } else { exit 0; } " +
                    "} catch { exit 0 }";
                PowerShellResult result = PowerShellRunner.Run(script);
                if (result.ExitCode == 1)
                {
                    InstalledVersion = (result.Output == null ? string.Empty : result.Output).Trim();
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The Store build can share the same package identity as the GitHub build;
        // SignatureKind is the reliable separator (Store vs Developer).
        internal static bool StorePackageInstalled()
        {
            try
            {
                PowerShellResult result = PowerShellRunner.Run(
                    "$ProgressPreference = 'SilentlyContinue'; " +
                    "$p = Get-AppxPackage -Name '*FluentFlyout*' -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.SignatureKind -eq 'Store' } | Select-Object -First 1; " +
                    "if ($p) { exit 1 } else { exit 0 }");
                return result.ExitCode == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ShowStoreNotice(bool uninstalling)
        {
            string text =
                "The Microsoft Store version of FluentFlyout is installed on this computer.\r\n\r\n" +
                "This tool only manages the unofficial GitHub sideload build. It will not update, launch, or uninstall the Store version.\r\n\r\n" +
                (uninstalling
                    ? "Nothing was changed. To remove the Store version, use Settings > Apps > Installed apps."
                    : "To switch to the GitHub sideload, uninstall the Store version first, then run this setup again.");
            MessageBox.Show(text, "FluentFlyout Sideloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        internal static string SelfPath
        {
            get
            {
                string p = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(p))
                {
                    try { p = Process.GetCurrentProcess().MainModule.FileName; }
                    catch (Exception) { }
                }
                return p;
            }
        }

        internal static byte[] EmbeddedResource(string name)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream stream = asm.GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded resource not found: " + name);
                }
                byte[] data = new byte[(int)stream.Length];
                stream.Read(data, 0, data.Length);
                return data;
            }
        }
    }

    internal sealed class PowerShellResult
    {
        internal readonly int ExitCode;
        internal readonly string Output;

        internal PowerShellResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            StringBuilder combined = new StringBuilder();
            if (!string.IsNullOrEmpty(output))
            {
                combined.AppendLine(output.TrimEnd());
            }
            if (!string.IsNullOrEmpty(error))
            {
                if (combined.Length > 0)
                {
                    combined.AppendLine();
                }
                combined.AppendLine(error.TrimEnd());
            }
            Output = combined.ToString();
        }
    }

    internal static class PowerShellRunner
    {
        private static readonly object ProcessLock = new object();
        private static Process _current;

        internal static void KillCurrent()
        {
            Process target;
            lock (ProcessLock)
            {
                target = _current;
            }
            if (target != null)
            {
                try
                {
                    if (!target.HasExited)
                    {
                        target.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        internal static PowerShellResult Run(string script)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "powershell.exe";
            // EncodedCommand avoids all command-line quoting pitfalls.
            psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();
            Process process = new Process();
            process.StartInfo = psi;
            process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    lock (stdout) { stdout.AppendLine(e.Data); }
                }
            };
            process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    lock (stderr) { stderr.AppendLine(e.Data); }
                }
            };

            process.Start();
            lock (ProcessLock) { _current = process; }
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception)
            {
            }

            bool exited = process.WaitForExit(15 * 60 * 1000);
            lock (ProcessLock)
            {
                if (object.ReferenceEquals(_current, process)) { _current = null; }
            }

            if (!exited)
            {
                try { process.Kill(); }
                catch (Exception) { }
                return new PowerShellResult(-99, stdout.ToString(), "Timed out waiting for PowerShell after 15 minutes.");
            }

            return new PowerShellResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }

    internal static class Operations
    {
        // ---- Install ----

        internal static string InstallScript(string bundlePath)
        {
            return
                "$ProgressPreference = 'SilentlyContinue'; " +
                "try { Add-AppxPackage -Path \"" + bundlePath + "\" -ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop; exit 0 } " +
                "catch { Write-Host $_.Exception.Message; exit 1 }";
        }

        internal static string LaunchScript()
        {
            // Launch through the AppsFolder shim so the app is NOT started with the
            // installer's elevated token (explorer de-elevates the launch).
            return
                "$ProgressPreference = 'SilentlyContinue'; " +
                "$pub = " + Entry.PsArray(Entry.GitHubPublishers()) + "; " +
                "try { " +
                "$pkg = Get-AppxPackage -Name '*FluentFlyout*' -ErrorAction SilentlyContinue | " +
                "Where-Object { $pub -contains $_.Publisher -and $_.SignatureKind -ne 'Store' } | " +
                "Sort-Object Version -Descending | Select-Object -First 1; " +
                "if ($pkg) { " +
                "$apps = $null; try { $apps = (Get-AppxPackageManifest $pkg).Package.Applications.Application } catch { }; " +
                "$first = $null; if ($apps -is [System.Array]) { $first = $apps[0] } else { $first = $apps }; " +
                "if ($first) { Start-Process -FilePath 'explorer.exe' -ArgumentList ('shell:AppsFolder\\' + $pkg.PackageFamilyName + '!' + $first.Id); } " +
                "} ; exit 0; } catch { exit 0 }";
        }

        internal static void TrustCert(X509Certificate2 cert)
        {
            if (IsCertTrusted(cert))
            {
                return; // keep reinstalls/updates idempotent
            }
            using (X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(cert);
            }
        }

        internal static void UntrustCert(X509Certificate2 cert)
        {
            RemoveCertByThumbprint(cert.Thumbprint);
        }

        internal static void WriteUninstallRegistry(long estimatedKb)
        {
            using (RegistryKey key = Registry.LocalMachine.CreateSubKey(Entry.UninstallKeyPath))
            {
                key.SetValue("DisplayName", Entry.DisplayName);
                key.SetValue("DisplayVersion", Entry.VersionText);
                key.SetValue("Publisher", Entry.Publisher);
                key.SetValue("DisplayIcon", Entry.SelfPath);
                key.SetValue("UninstallString", "\"" + Entry.SelfPath + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)estimatedKb, RegistryValueKind.DWord);
            }
        }

        internal static void DeleteUninstallRegistry()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(Entry.UninstallKeyPath, false);
            }
            catch (Exception)
            {
            }
        }

        // ---- Uninstall ----

        internal static string UninstallScript()
        {
            return
                "$ProgressPreference = 'SilentlyContinue'; " +
                "$pub = " + Entry.PsArray(Entry.GitHubPublishers()) + "; " +
                "try { " +
                "$pkgs = Get-AppxPackage -Name '*FluentFlyout*' -ErrorAction SilentlyContinue | " +
                "Where-Object { $pub -contains $_.Publisher -and $_.SignatureKind -ne 'Store' }; " +
                "if ($pkgs) { $pkgs | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -ErrorAction Stop; } }; " +
                "exit 0; " +
                "} catch { Write-Host $_.Exception.Message; exit 1 }";
        }

        // ---- Update plumbing ----

        internal const string TaskName = "FluentFlyoutUpdateCheck";

        internal static void RefreshUninstallRegistry(string version, long estimatedKb)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(Entry.UninstallKeyPath))
                {
                    if (!string.IsNullOrEmpty(version))
                    {
                        key.SetValue("DisplayVersion", version);
                    }
                    key.SetValue("EstimatedSize", (int)estimatedKb, RegistryValueKind.DWord);
                }
            }
            catch (Exception)
            {
                // Cosmetic only; non-elevated update paths cannot write HKLM.
            }
        }

        internal static void CreateUpdateTask()
        {
            try
            {
                string exe = Entry.SelfPath;
                string script =
                    "$tr = '\"' + '" + exe + "' + '\" /check'; " +
                    "& schtasks.exe /Create /F /SC DAILY /ST 09:00 /TN '" + TaskName + "' /TR $tr; exit $LASTEXITCODE";
                PowerShellRunner.Run(script);
            }
            catch (Exception)
            {
                // Non-fatal: updates fall back to manual checks only.
            }
        }

        internal static void DeleteUpdateTask()
        {
            try
            {
                PowerShellRunner.Run("& schtasks.exe /Delete /TN '" + TaskName + "' /F; exit 0");
            }
            catch (Exception)
            {
            }
        }

        // True when this exact certificate is already in the machine root store.
        internal static bool IsCertTrusted(X509Certificate2 cert)
        {
            try
            {
                using (X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 existing in store.Certificates)
                    {
                        if (string.Equals(existing.Thumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // Removes every root-store entry with this thumbprint; safe when absent.
        internal static bool RemoveCertByThumbprint(string thumbprint)
        {
            if (thumbprint == null || thumbprint.Length == 0)
            {
                return false;
            }
            bool removed = false;
            try
            {
                using (X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    while (true)
                    {
                        X509Certificate2 found = null;
                        foreach (X509Certificate2 existing in store.Certificates)
                        {
                            if (string.Equals(existing.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                            {
                                found = existing;
                                break;
                            }
                        }
                        if (found == null)
                        {
                            break;
                        }
                        store.Remove(found);
                        removed = true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return removed;
        }
    }

    // ================= Automatic updates =================

    internal sealed class UpdateInfo
    {
        internal readonly string Version;      // latest release tag, e.g. "2.17.0"

        internal UpdateInfo(string version)
        {
            Version = version;
        }
    }

    internal static class VersionHelper
    {
        // Parses "v2.17.1", "2.17.1", "2.17.1.0" into numeric tuples; null when not parsable.
        internal static int[] Parse(string text)
        {
            if (text == null)
            {
                return null;
            }
            string t = text.Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(1);
            }
            string[] parts = t.Split('.');
            List<int> nums = new List<int>();
            foreach (string part in parts)
            {
                int n;
                string p = part.Trim();
                int len = 0;
                while (len < p.Length && p[len] >= '0' && p[len] <= '9')
                {
                    len++;
                }
                if (len == 0)
                {
                    break;
                }
                if (int.TryParse(p.Substring(0, len), out n))
                {
                    nums.Add(n);
                }
                else
                {
                    break;
                }
            }
            if (nums.Count == 0)
            {
                return null;
            }
            return nums.ToArray();
        }

        internal static int Compare(string a, string b)
        {
            int[] va = Parse(a);
            int[] vb = Parse(b);
            if (va == null && vb == null)
            {
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
            if (va == null) { return -1; }
            if (vb == null) { return 1; }
            int len = va.Length > vb.Length ? va.Length : vb.Length;
            for (int i = 0; i < len; i++)
            {
                int x = i < va.Length ? va[i] : 0;
                int y = i < vb.Length ? vb[i] : 0;
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }
            }
            return 0;
        }
    }

    internal static class UpdateClient
    {
        private const string ApiLatest = "https://api.github.com/repos/unchihugo/FluentFlyout/releases/latest";

        // GitHub API for the latest release tag only. The repository no longer
        // attaches installer files to releases (the ZIP moved to fluentflyout.com),
        // so the version comes from here and the files from the website feed.
        internal static UpdateInfo TryCheck(out string error)
        {
            error = null;
            PinTls12();
            string tag = null;
            string detail = null;
            try
            {
                try { tag = FetchTagFromApi(); }
                catch (Exception ex) { detail = "API: " + ex.ToString(); }

                if (tag == null)
                {
                    // Fallback: the releases page itself redirects to /tag/<version>.
                    try { tag = FetchTagFromRedirect(); }
                    catch (Exception ex) { detail = (detail == null ? "" : detail + "\r\n") + "PAGE: " + ex.ToString(); }
                }

                if (tag == null)
                {
                    error = "The release feed could not be reached.";
                    UpdateState.SaveCheckError(
                        "UTC " + DateTime.UtcNow.ToString("u") + "\r\nNo version tag could be read.\r\n" +
                        (detail == null ? "" : detail));
                    return null;
                }

                UpdateState.ClearCheckError();
                return new UpdateInfo(tag);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                UpdateState.SaveCheckError(
                    "UTC " + DateTime.UtcNow.ToString("u") + "\r\n" + ex.ToString() +
                    (detail == null ? "" : "\r\n--- earlier ---\r\n" + detail));
                return null;
            }
        }

        private static void PinTls12()
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // Tls12
            }
            catch (Exception)
            {
            }
        }

        private static string FetchTagFromApi()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApiLatest);
            request.UserAgent = "FluentFlyout-Sideloader/" + Entry.VersionText;
            request.Accept = "application/vnd.github+json";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.Timeout = 12000;
            request.ReadWriteTimeout = 12000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success)
                {
                    throw new Exception("The API response contained no tag (rate limited?).");
                }
                return CleanTag(m.Groups[1].Value);
            }
        }

        private static string FetchTagFromRedirect()
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                "https://github.com/unchihugo/FluentFlyout/releases/latest");
            request.UserAgent = "FluentFlyout-Sideloader/" + Entry.VersionText;
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                string path = response.ResponseUri != null ? response.ResponseUri.AbsolutePath : null;
                if (path == null)
                {
                    throw new Exception("The redirect did not reveal a tag.");
                }
                string marker = "/tag/";
                int idx = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    throw new Exception("The redirect did not reveal a tag.");
                }
                return CleanTag(path.Substring(idx + marker.Length));
            }
        }

        private static string CleanTag(string tag)
        {
            string t = tag == null ? string.Empty : tag.Trim();
            if (t.Length > 1 && (t[0] == 'v' || t[0] == 'V'))
            {
                t = t.Substring(1);
            }
            return t;
        }

        // The website feed always serves the current GitHub-build installer kit as a
        // zip (bundle + certificate inside); this URL tracks the latest version.
        internal const string DownloadUrl = "https://fluentflyout.com/files/latest/x64";

        // Streams url into destPath; returns the final URL after redirects so the
        // caller can see which file version the site actually served.
        internal static string Download(string url, string destPath, Action<long, long> progress)
        {
            PinTls12();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "FluentFlyout-Sideloader/" + Entry.VersionText;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 180000;
            string finalUrl = url;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ResponseUri != null)
                {
                    finalUrl = response.ResponseUri.ToString();
                }
                long total = 0;
                try { total = response.ContentLength; } catch (Exception) { }
                using (Stream input = response.GetResponseStream())
                using (FileStream output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    byte[] buffer = new byte[256 * 1024];
                    long done = 0;
                    while (true)
                    {
                        int n = input.Read(buffer, 0, buffer.Length);
                        if (n <= 0)
                        {
                            break;
                        }
                        output.Write(buffer, 0, n);
                        done += n;
                        if (progress != null)
                        {
                            progress(done, total > 0 ? total : done);
                        }
                    }
                }
            }
            return finalUrl;
        }

        // Pulls just the msixbundle and certificate entries out of the installer zip.
        internal static void ExtractUpdateFiles(string zipPath, string destDir, out string bundlePath, out string certPath)
        {
            Directory.CreateDirectory(destDir);
            bundlePath = Path.Combine(destDir, "update.msixbundle");
            certPath = Path.Combine(destDir, "update.cer");
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry bundleEntry = null;
                ZipArchiveEntry certEntry = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName;
                    if (bundleEntry == null &&
                        (name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)))
                    {
                        bundleEntry = entry;
                    }
                    else if (certEntry == null && name.EndsWith(".cer", StringComparison.OrdinalIgnoreCase))
                    {
                        certEntry = entry;
                    }
                }
                if (bundleEntry == null)
                {
                    throw new Exception("The downloaded package does not contain an .msixbundle or .msix file.");
                }
                if (certEntry == null)
                {
                    throw new Exception("The downloaded package does not contain a .cer certificate file.");
                }
                bundleEntry.ExtractToFile(bundlePath, true);
                certEntry.ExtractToFile(certPath, true);
            }
        }

        // Reads a version out of the served file name,
        // e.g. ".../FluentFlyout_2.16.0_x64_Installer.zip" -> "2.16.0".
        internal static string VersionFromFileName(string urlOrName)
        {
            try
            {
                string name = urlOrName;
                Uri uri;
                if (Uri.TryCreate(name, UriKind.Absolute, out uri))
                {
                    string last = uri.Segments[uri.Segments.Length - 1];
                    name = Uri.UnescapeDataString(last);
                }
                Match m = Regex.Match(name, @"\d+(?:\.\d+)+");
                if (m.Success)
                {
                    return m.Value;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }

    internal static class UpdateState
    {
        internal static string StoreDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FluentFlyoutSetup");
            }
        }

        internal static string DownloadsDir
        {
            get { return Path.Combine(StoreDir, "downloads"); }
        }

        internal static string LastCertPath
        {
            get { return Path.Combine(StoreDir, "last-release.cer"); }
        }

        internal static string LastThumbprintPath
        {
            get { return Path.Combine(StoreDir, "last-release.txt"); }
        }

        internal static string LastCheckStampPath
        {
            get { return Path.Combine(StoreDir, "last-check.txt"); }
        }

        internal static string LastCheckErrorPath
        {
            get { return Path.Combine(StoreDir, "last-check-error.txt"); }
        }

        // Diagnostics: keep the most recent check failure around for support.
        internal static void SaveCheckError(string text)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(LastCheckErrorPath, text);
            }
            catch (Exception)
            {
            }
        }

        internal static void ClearCheckError()
        {
            try
            {
                File.Delete(LastCheckErrorPath);
            }
            catch (Exception)
            {
            }
        }

        // Called after an update applies so uninstall can remove the NEWEST cert too.
        internal static void SaveCert(string sourceCertPath)
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.Copy(sourceCertPath, LastCertPath, true);
                using (X509Certificate2 cert = new X509Certificate2(sourceCertPath))
                {
                    List<string> thumbprints = new List<string>();
                    try
                    {
                        foreach (string line in File.ReadAllLines(LastThumbprintPath))
                        {
                            string t = line.Trim();
                            if (t.Length > 0 && !thumbprints.Contains(t))
                            {
                                thumbprints.Add(t);
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                    if (!thumbprints.Contains(cert.Thumbprint))
                    {
                        thumbprints.Add(cert.Thumbprint);
                    }
                    File.WriteAllLines(LastThumbprintPath, thumbprints.ToArray());
                }
            }
            catch (Exception)
            {
            }
        }

        internal static string[] LastCertThumbprints()
        {
            try
            {
                List<string> thumbprints = new List<string>();
                foreach (string line in File.ReadAllLines(LastThumbprintPath))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !thumbprints.Contains(t))
                    {
                        thumbprints.Add(t);
                    }
                }
                return thumbprints.ToArray();
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        internal static void MarkCheckTime()
        {
            try
            {
                Directory.CreateDirectory(StoreDir);
                File.WriteAllText(LastCheckStampPath, DateTime.UtcNow.Ticks.ToString());
            }
            catch (Exception)
            {
            }
        }

        internal static bool RecentlyChecked()
        {
            try
            {
                string text = File.ReadAllText(LastCheckStampPath).Trim();
                long ticks;
                if (!long.TryParse(text, out ticks))
                {
                    return false;
                }
                DateTime last = new DateTime(ticks, DateTimeKind.Utc);
                return DateTime.UtcNow - last < TimeSpan.FromMinutes(30);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ClearAll()
        {
            try
            {
                if (Directory.Exists(StoreDir))
                {
                    Directory.Delete(StoreDir, true);
                }
            }
            catch (Exception)
            {
            }
        }
    }

    // ================= Modern UI =================

    internal static class UiPaint
    {
        internal static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            GraphicsPath path = new GraphicsPath();
            if (w <= 0f || h <= 0f)
            {
                return path;
            }
            if (r > w / 2f) { r = w / 2f; }
            if (r > h / 2f) { r = h / 2f; }
            if (r <= 0.5f)
            {
                path.AddRectangle(new RectangleF(x, y, w, h));
                return path;
            }
            float d = r * 2f;
            path.AddArc(x, y, d, d, 180f, 90f);
            path.AddArc(x + w - d, y, d, d, 270f, 90f);
            path.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            path.AddArc(x, y + h - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class UiTheme
    {
        internal readonly Color WindowBg;
        internal readonly Color Foreground;
        internal readonly Color DimText;
        internal readonly Color TrackColor;
        internal readonly Color Accent;
        internal readonly Color AccentHover;
        internal readonly Color AccentDown;
        internal readonly Color ButtonText;
        internal readonly Color NeutralFill;
        internal readonly Color NeutralHover;
        internal readonly Color NeutralDown;
        internal readonly Color Outline;
        internal readonly Color Success;
        internal readonly Color Danger;
        internal readonly Color CloseGlyphIdle;

        private UiTheme(Color windowBg, Color foreground, Color dimText, Color trackColor,
            Color accent, Color accentHover, Color accentDown, Color buttonText,
            Color neutralFill, Color neutralHover, Color neutralDown,
            Color outline, Color success, Color danger, Color closeGlyphIdle)
        {
            WindowBg = windowBg;
            Foreground = foreground;
            DimText = dimText;
            TrackColor = trackColor;
            Accent = accent;
            AccentHover = accentHover;
            AccentDown = accentDown;
            ButtonText = buttonText;
            NeutralFill = neutralFill;
            NeutralHover = neutralHover;
            NeutralDown = neutralDown;
            Outline = outline;
            Success = success;
            Danger = danger;
            CloseGlyphIdle = closeGlyphIdle;
        }

        // Win11 light dialog palette.
        internal static readonly UiTheme Light = new UiTheme(
            Color.FromArgb(243, 243, 243),
            Color.FromArgb(26, 26, 26),
            Color.FromArgb(97, 97, 97),
            Color.FromArgb(222, 222, 222),
            Color.FromArgb(0, 95, 184),
            Color.FromArgb(21, 117, 201),
            Color.FromArgb(0, 78, 140),
            Color.White,
            Color.FromArgb(230, 230, 230),
            Color.FromArgb(216, 216, 216),
            Color.FromArgb(193, 193, 193),
            Color.FromArgb(213, 213, 213),
            Color.FromArgb(15, 123, 15),
            Color.FromArgb(196, 43, 28),
            Color.FromArgb(110, 110, 110));

        // Win11 dark dialog palette.
        internal static readonly UiTheme Dark = new UiTheme(
            Color.FromArgb(32, 32, 32),
            Color.White,
            Color.FromArgb(160, 160, 160),
            Color.FromArgb(61, 61, 61),
            Color.FromArgb(76, 194, 255),
            Color.FromArgb(102, 205, 255),
            Color.FromArgb(64, 172, 232),
            Color.FromArgb(0, 0, 0),
            Color.FromArgb(58, 58, 58),
            Color.FromArgb(70, 70, 70),
            Color.FromArgb(47, 47, 47),
            Color.FromArgb(51, 51, 51),
            Color.FromArgb(108, 203, 95),
            Color.FromArgb(196, 43, 28),
            Color.FromArgb(230, 230, 230));

        internal static UiTheme Load()
        {
            bool dark = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int && ((int)v) == 0)
                        {
                            dark = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return dark ? Dark : Light;
        }
    }

    // Slim animated progress bar (4px-ish, rounded), replaces the chunky ProgressBar.
    internal sealed class MarqueeBar : Control
    {
        private readonly WinFormsTimer _timer;
        private double _phase;
        private bool _running;
        private bool _determinate;
        private double _fraction;

        internal MarqueeBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            _timer = new WinFormsTimer();
            _timer.Interval = 16;
            _timer.Tick += delegate
            {
                _phase += 0.011;
                if (_phase >= 1.0) { _phase -= 1.0; }
                Invalidate();
            };
        }

        internal void Start()
        {
            if (!_running)
            {
                _running = true;
                _timer.Start();
            }
        }

        internal void Stop()
        {
            _running = false;
            _timer.Stop();
        }

        internal void SetDeterminate(double fraction)
        {
            if (fraction < 0.0) { fraction = 0.0; }
            if (fraction > 1.0) { fraction = 1.0; }
            _determinate = true;
            _running = false;
            _timer.Stop();
            _fraction = fraction;
            Invalidate();
        }

        internal void SetIndeterminate()
        {
            _determinate = false;
            _phase = 0;
            Start();
            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { Start(); }
            else { Stop(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush back = new SolidBrush(Parent != null ? Parent.BackColor : Color.Transparent))
            {
                g.FillRectangle(back, ClientRectangle);
            }
            float barH = 6f;
            float y = (Height - barH) / 2f;
            using (GraphicsPath track = UiPaint.RoundRect(0f, y, Width, barH, barH / 2f))
            using (SolidBrush trackBrush = new SolidBrush(Theme.Current.TrackColor))
            {
                g.FillPath(trackBrush, track);
            }
            if (_determinate)
            {
                float fillW = (float)(_fraction * Width);
                if (fillW > 1f)
                {
                    using (GraphicsPath fillPath = UiPaint.RoundRect(0f, y, fillW, barH, barH / 2f))
                    using (SolidBrush fillBrush = new SolidBrush(Theme.Current.Accent))
                    {
                        g.FillPath(fillBrush, fillPath);
                    }
                }
                return;
            }
            float seg = Width / 4f;
            float x = (float)(_phase * 1.35) * (Width + seg) - seg; // eased wrap keeps motion smooth
            if (x > Width) { x = Width; }
            Graphics gClip = g;
            using (GraphicsPath block = UiPaint.RoundRect(x, y, seg, barH, barH / 2f))
            using (SolidBrush barBrush = new SolidBrush(Theme.Current.Accent))
            {
                gClip.FillPath(barBrush, block);
            }
        }
    }

    // Circled status glyph: none / success check / error cross.
    internal sealed class StatusIcon : Control
    {
        internal const int StateNone = 0;
        internal const int StateSuccess = 1;
        internal const int StateError = 2;

        internal int State;

        internal StatusIcon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            State = StateNone;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (State == StateNone)
            {
                return;
            }
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush back = new SolidBrush(Parent != null ? Parent.BackColor : Color.Transparent))
            {
                g.FillRectangle(back, ClientRectangle);
            }
            float d = Math.Min(Width, Height) - 2f;
            float x = (Width - d) / 2f;
            float y = (Height - d) / 2f;
            Color fill = State == StateSuccess ? Theme.Current.Success : Theme.Current.Danger;
            using ( SolidBrush b = new SolidBrush(fill))
            {
                g.FillEllipse(b, x, y, d, d);
            }
            using (Pen pen = new Pen(Color.White, 2f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (State == StateSuccess)
                {
                    g.DrawLines(pen, new PointF[]
                    {
                        new PointF(x + d * 0.26f, y + d * 0.52f),
                        new PointF(x + d * 0.44f, y + d * 0.70f),
                        new PointF(x + d * 0.76f, y + d * 0.32f)
                    });
                }
                else
                {
                    g.DrawLine(pen, x + d * 0.30f, y + d * 0.30f, x + d * 0.70f, y + d * 0.70f);
                    g.DrawLine(pen, x + d * 0.70f, y + d * 0.30f, x + d * 0.30f, y + d * 0.70f);
                }
            }
        }
    }

    // Win11-style accent button (rounded, hover/press states).
    internal sealed class FlatButton : Control
    {
        private bool _hover;
        private bool _down;
        private Color _fill;
        private Color _fillHover;
        private Color _fillDown;
        private Color _text;

        internal FlatButton(Color fill, Color fillHover, Color fillDown, Color text)
        {
            _fill = fill;
            _fillHover = fillHover;
            _fillDown = fillDown;
            _text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _down = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _down = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _down = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush back = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Current.WindowBg))
            {
                g.FillRectangle(back, ClientRectangle);
            }
            Color fill = _fill;
            if (_down) { fill = _fillDown; }
            else if (_hover) { fill = _fillHover; }
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath path = UiPaint.RoundRect(r.X, r.Y, r.Width, r.Height, 5f))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, _text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    // Small "x" glyph button for the window corner (hover = red, like native close).
    internal sealed class GlyphButton : Control
    {
        private bool _hover;

        internal GlyphButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UiTheme t = Theme.Current;
            if (_hover)
            {
                using (SolidBrush b = new SolidBrush(t.Danger))
                {
                    g.FillRectangle(b, ClientRectangle);
                }
            }
            else
            {
                using (SolidBrush back = new SolidBrush(Parent != null ? Parent.BackColor : t.WindowBg))
                {
                    g.FillRectangle(back, ClientRectangle);
                }
            }
            float cx = Width / 2f;
            float cy = Height / 2f;
            float s = Math.Min(Width, Height) * 0.24f;
            using (Pen pen = new Pen(_hover ? Color.White : t.CloseGlyphIdle, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
            }
        }
    }

    // Static access to the active palette (set once by SetupForm).
    internal static class Theme
    {
        internal static UiTheme Current = UiTheme.Load();
    }

    internal sealed class SetupForm : Form
    {
        // Design-space metrics (96 dpi baseline), scaled at runtime.
        private const int DesignW = 460;
        private const int DesignH = 276;
        private const int DragStripHeight = 46;

        private readonly bool _uninstall;
        private readonly bool _preview;
        private readonly UiTheme _theme;
        private float _scale = 1f;

        private GlyphButton _glyphClose;
        private FlatButton _closeBtn;
        private Label _statusLabel;
        private StatusIcon _icon;
        private MarqueeBar _bar;

        private Font _titleFont;
        private Font _bodyFont;
        private Font _subFont;
        private Font _badgeFont;

        private Image _logo;
        private MemoryStream _logoStream;

        private bool _workFinished;
        private WinFormsTimer _autoClose;
        private WinFormsTimer _fadeTimer;
        private double _fadeStep;

        internal SetupForm(bool uninstall)
            : this(uninstall, false)
        {
        }

        internal SetupForm(bool uninstall, bool preview)
        {
            _uninstall = uninstall;
            _preview = preview;
            _theme = Theme.Current;

            Text = preview ? "FluentFlyout Sideloader Setup (preview)"
                 : uninstall ? "Uninstall FluentFlyout" : "FluentFlyout Sideloader Setup";
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = _theme.WindowBg;
            ForeColor = _theme.Foreground;
            ClientSize = new Size(DesignW, DesignH);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Load += delegate
            {
                MeasureScale();
                BuildLayout();
                StartWork();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void MeasureScale()
        {
            _scale = 1f;
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    float dpi = g.DpiY;
                    if (dpi > 96f) { _scale = dpi / 96f; }
                }
            }
            catch (Exception)
            {
            }
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private void BuildLayout()
        {
            // Scale the whole window and all pixel bounds to the current DPI.
            ClientSize = new Size(S(DesignW), S(DesignH));

            _titleFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            _bodyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _subFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _badgeFont = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point);

            // Load the embedded app logo (fall back to the painted "F" badge if absent).
            try
            {
                _logoStream = new MemoryStream(Entry.EmbeddedResource(Entry.ResLogo));
                _logo = Image.FromStream(_logoStream);
                _logoStream.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                _logo = null;
                _logoStream = null;
            }

            _glyphClose = new GlyphButton();
            _glyphClose.SetBounds(S(DesignW - 48), S(2), S(44), S(30));
            _glyphClose.Click += delegate { OnCloseRequested(); };
            Controls.Add(_glyphClose);

            _icon = new StatusIcon();
            _icon.State = StatusIcon.StateNone;
            _icon.Visible = false;
            _icon.SetBounds(S(26), S(122), S(24), S(24));
            _icon.BackColor = _theme.WindowBg;
            Controls.Add(_icon);

            _statusLabel = new Label();
            _statusLabel.Text = "Preparing...";
            _statusLabel.Font = _bodyFont;
            _statusLabel.ForeColor = _theme.Foreground;
            _statusLabel.BackColor = _theme.WindowBg;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.SetBounds(S(26), S(118), S(410), S(46));
            Controls.Add(_statusLabel);

            _bar = new MarqueeBar();
            _bar.SetBounds(S(26), S(176), S(410), S(10));
            _bar.BackColor = _theme.WindowBg;
            Controls.Add(_bar);

            _closeBtn = new FlatButton(_theme.Accent, _theme.AccentHover, _theme.AccentDown, _theme.ButtonText);
            _closeBtn.Text = "Close";
            _closeBtn.Font = _bodyFont;
            _closeBtn.SetBounds(S(DesignW - 26 - 104), S(DesignH - 24 - 34), S(104), S(34));
            _closeBtn.Visible = false;
            _closeBtn.Click += delegate { Close(); };
            Controls.Add(_closeBtn);

            Invalidate();
        }

        // ---- Window chrome: drag area, fade-in, rounded corners ----

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTCAPTION = 2;
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                try
                {
                    if ((int)m.Result == HTCLIENT)
                    {
                        Point p = PointToClient(Cursor.Position);
                        if (p.Y < S(DragStripHeight) && !_glyphClose.Bounds.Contains(p))
                        {
                            m.Result = (IntPtr)HTCAPTION;
                        }
                    }
                }
                catch (Exception)
                {
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception)
            {
            }
            StartFadeIn();
        }

        private void StartFadeIn()
        {
            Opacity = 0.0;
            _fadeStep = 0;
            _fadeTimer = new WinFormsTimer();
            _fadeTimer.Interval = 15;
            _fadeTimer.Tick += delegate
            {
                _fadeStep += 1.0;
                double a = _fadeStep / 10.0;
                if (a >= 1.0)
                {
                    Opacity = 1.0;
                    _fadeTimer.Stop();
                    _fadeTimer.Dispose();
                    _fadeTimer = null;
                }
                else
                {
                    Opacity = a;
                }
            };
            _fadeTimer.Start();
        }

        private void OnCloseRequested()
        {
            if (!_workFinished)
            {
                DialogResult dr = MessageBox.Show(
                    this,
                    "Setup is still running. Quit and cancel?",
                    "FluentFlyout Sideloader Setup",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes)
                {
                    return;
                }
                PowerShellRunner.KillCurrent();
                Environment.Exit(0);
            }
            Close();
        }

        // ---- Worker plumbing ----

        private void StartWork()
        {
            if (_preview)
            {
                // Sample "completed install" view; no worker, no system changes, no auto-close.
                ShowResult(
                    "FluentFlyout " + Entry.VersionText + " was installed successfully.",
                    "Preview mode - no changes were made on this computer.",
                    false);
                return;
            }
            ThreadStart start = _uninstall ? new ThreadStart(RunUninstallWork) : new ThreadStart(RunInstallWork);
            Thread worker = new Thread(start);
            worker.IsBackground = true;
            worker.TrySetApartmentState(ApartmentState.MTA);
            worker.Start();
        }
        private void PostUI(MethodInvoker action)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }

        private void SetStatus(string text)
        {
            PostUI(delegate { _statusLabel.Text = text; });
        }

        private void ShowResult(string firstLine, string secondLine, bool isError)
        {
            _workFinished = true;
            PostUI(delegate
            {
                _bar.Stop();
                _bar.Visible = false;
                _icon.State = isError ? StatusIcon.StateError : StatusIcon.StateSuccess;
                _icon.Visible = true;
                _statusLabel.SetBounds(S(58), S(118), S(378), S(46));
                _statusLabel.ForeColor = isError ? _theme.Danger : _theme.Foreground;
                _statusLabel.Text = firstLine + "\r\n" + secondLine;
                _closeBtn.Visible = true;
            });
        }

        private void Fail(string details, string message)
        {
            ShowResult(message, "See the error dialog for details.", true);
            MessageBox.Show(
                Truncate(details == null ? "" : details.Trim(), 1800),
                _uninstall ? "FluentFlyout Uninstaller" : "FluentFlyout Sideloader Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private static string Truncate(string text, int max)
        {
            if (text.Length <= max) { return text; }
            return text.Substring(0, max) + "\r\n\r\n... (output truncated)";
        }

        // ---- Requested close: auto-close timer for success ----

        private void SetupAutoClose(int intervalMs)
        {
            if (_autoClose != null)
            {
                return;
            }
            _autoClose = new WinFormsTimer();
            _autoClose.Interval = intervalMs;
            _autoClose.Tick += delegate
            {
                _autoClose.Stop();
                if (_workFinished)
                {
                    Close();
                }
            };
            _autoClose.Start();
        }

        // ---- Install worker ----

        private void RunInstallWork()
        {
            const int totalSteps = 4;
            string tempDir = null;
            try
            {
                if (!Entry.IsAppInstalled() && Entry.StorePackageInstalled())
                {
                    ShowResult(
                        "The Microsoft Store version is installed.",
                        "This tool only manages the GitHub sideload. Nothing was changed.",
                        true);
                    return;
                }

                SetStatus("Step 1 of " + totalSteps + ": extracting installation files...");
                tempDir = NewTempDir();

                byte[] certBytes = Entry.EmbeddedResource(Entry.ResCert);
                byte[] bundleBytes = Entry.EmbeddedResource(Entry.ResBundle);

                string certPath = Path.Combine(tempDir, "FluentFlyoutGitHub.cer");
                string bundlePath = Path.Combine(tempDir, "FluentFlyoutGitHub.msixbundle");
                File.WriteAllBytes(certPath, certBytes);
                File.WriteAllBytes(bundlePath, bundleBytes);

                SetStatus("Step 2 of " + totalSteps + ": trusting the FluentFlyout certificate...");
                using (X509Certificate2 cert = new X509Certificate2(certBytes))
                {
                    Operations.TrustCert(cert);
                }

                SetStatus("Step 3 of " + totalSteps + ": installing FluentFlyout - a running copy will restart...");
                PowerShellResult result = PowerShellRunner.Run(Operations.InstallScript(bundlePath));
                if (result.ExitCode != 0)
                {
                    Fail(result.Output, "FluentFlyout could not be installed.");
                    Cleanup(tempDir);
                    return;
                }

                SetStatus("Step 4 of " + totalSteps + ": finishing installation...");
                long estimatedKb = bundleBytes.Length / 1024L;
                Operations.WriteUninstallRegistry(estimatedKb);
                PowerShellRunner.Run(Operations.LaunchScript());
                Operations.CreateUpdateTask();

                Cleanup(tempDir);
                ShowResult(
                    "FluentFlyout " + Entry.VersionText + " was installed successfully.",
                    "It is running now - look for the tray icon. Uninstall via Settings > Apps.",
                    false);
                SetupAutoClose(5000);
            }
            catch (Exception ex)
            {
                Fail(ex.Message, "Installation failed.");
                Cleanup(tempDir);
            }
        }

        // ---- Uninstall worker ----

        private void RunUninstallWork()
        {
            const int totalSteps = 3;
            try
            {
                if (!Entry.IsAppInstalled() && Entry.StorePackageInstalled())
                {
                    ShowResult(
                        "The Microsoft Store version is installed.",
                        "This tool only removes the GitHub sideload. Nothing was changed.",
                        true);
                    return;
                }

                SetStatus("Step 1 of " + totalSteps + ": removing FluentFlyout...");
                PowerShellResult result = PowerShellRunner.Run(Operations.UninstallScript());
                if (result.ExitCode != 0)
                {
                    Fail(result.Output, "FluentFlyout could not be removed.");
                    return;
                }

                SetStatus("Step 2 of " + totalSteps + ": removing certificates, task and update data...");
                try
                {
                    byte[] certBytes = Entry.EmbeddedResource(Entry.ResCert);
                    using (X509Certificate2 cert = new X509Certificate2(certBytes))
                    {
                        Operations.UntrustCert(cert);
                    }
                }
                catch (Exception)
                {
                    // Non-fatal: maybe removed manually earlier.
                }
                try
                {
                    // Past updates may have installed newer release certificates.
                    foreach (string thumbprint in UpdateState.LastCertThumbprints())
                    {
                        Operations.RemoveCertByThumbprint(thumbprint);
                    }
                }
                catch (Exception)
                {
                }
                Operations.DeleteUpdateTask();
                UpdateState.ClearAll();

                SetStatus("Step 3 of " + totalSteps + ": removing the uninstall entry...");
                Operations.DeleteUninstallRegistry();

                ShowResult(
                    "FluentFlyout was removed from this computer.",
                    "The signing certificate and the Apps-list entry were cleaned up.",
                    false);
                SetupAutoClose(5000);
            }
            catch (Exception ex)
            {
                Fail(ex.Message, "Uninstallation failed.");
            }
        }

        // ---- Artwork painted on the form ----

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Window outline.
            using (Pen pen = new Pen(_theme.Outline, 1f))
            using (GraphicsPath border = UiPaint.RoundRect(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f, 8f))
            {
                g.DrawPath(pen, border);
            }

            // App badge: the real logo, or a painted accent tile with an "F" as fallback.
            float bx = S(26);
            float by = S(54);
            float bd = S(46);
            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_logo, bx, by, bd, bd);
            }
            else
            {
                using (GraphicsPath badge = UiPaint.RoundRect(bx, by, bd, bd, S(12)))
                using (SolidBrush fill = new SolidBrush(_theme.Accent))
                {
                    g.FillPath(fill, badge);
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush glyph = new SolidBrush(Color.White))
                    {
                        g.DrawString("F", _badgeFont, glyph,
                            new RectangleF(bx, by - S(1), bd, bd), sf);
                    }
                }
            }

            // Title + subtitle.
            using (SolidBrush fg = new SolidBrush(_theme.Foreground))
            {
                g.DrawString(_uninstall ? "Uninstall FluentFlyout" : "Install FluentFlyout",
                    _titleFont, fg, S(84), S(56));
            }
            using (SolidBrush dim = new SolidBrush(_theme.DimText))
            {
                g.DrawString("Version " + Entry.VersionText + "  |  Unofficial GitHub release installer",
                    _subFont, dim, S(86), S(88));
            }
        }

        // ---- helpers ----

        private static string NewTempDir()
        {
            string dir = Path.Combine(
                Path.GetTempPath(),
                "FluentFlyoutSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            if (dir == null) { return; }
            try { Directory.Delete(dir, true); }
            catch (Exception) { }
        }
    }

    // Maintenance chooser: shown (non-elevated) when the app is already installed.
    // Offers reinstall (full repair of the same release) or uninstall (app + cert + entry).
    internal sealed class ChooserForm : Form
    {
        private const int DesignW = 460;
        private const int DesignH = 268;
        private const int DragStripHeight = 46;

        private readonly UiTheme _theme;
        private float _scale = 1f;

        private GlyphButton _glyphClose;
        private FlatButton _reinstallBtn;
        private FlatButton _uninstallBtn;
        private Label _bodyLabel;
        private Label _updateStatusLabel;
        private FlatButton _updateBtn;
        private UpdateInfo _updateInfo;

        private Font _titleFont;
        private Font _bodyFont;
        private Font _subFont;
        private Font _badgeFont;

        private Image _logo;
        private MemoryStream _logoStream;

        internal ChooserForm()
        {
            _theme = Theme.Current;

            Text = "FluentFlyout Sideloader";
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = _theme.WindowBg;
            ForeColor = _theme.Foreground;
            ClientSize = new Size(DesignW, DesignH);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Load += delegate
            {
                MeasureScale();
                BuildLayout();
                StartUpdateCheck();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception)
            {
            }
            StartFadeIn();
        }

        private void StartFadeIn()
        {
            Opacity = 0.0;
            double step = 0;
            WinFormsTimer timer = new WinFormsTimer();
            timer.Interval = 15;
            timer.Tick += delegate
            {
                step += 1.0;
                double a = step / 10.0;
                if (a >= 1.0)
                {
                    Opacity = 1.0;
                    timer.Stop();
                    timer.Dispose();
                }
                else
                {
                    Opacity = a;
                }
            };
            timer.Start();
        }

        private void MeasureScale()
        {
            _scale = 1f;
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    float dpi = g.DpiY;
                    if (dpi > 96f) { _scale = dpi / 96f; }
                }
            }
            catch (Exception)
            {
            }
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTCAPTION = 2;
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                try
                {
                    if ((int)m.Result == HTCLIENT)
                    {
                        Point p = PointToClient(Cursor.Position);
                        if (p.Y < S(DragStripHeight) && !_glyphClose.Bounds.Contains(p))
                        {
                            m.Result = (IntPtr)HTCAPTION;
                        }
                    }
                }
                catch (Exception)
                {
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void BuildLayout()
        {
            ClientSize = new Size(S(DesignW), S(DesignH));

            _titleFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            _bodyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _subFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _badgeFont = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point);

            try
            {
                _logoStream = new MemoryStream(Entry.EmbeddedResource(Entry.ResLogo));
                _logo = Image.FromStream(_logoStream);
                _logoStream.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                _logo = null;
                _logoStream = null;
            }

            _glyphClose = new GlyphButton();
            _glyphClose.SetBounds(S(DesignW - 48), S(2), S(44), S(30));
            _glyphClose.Click += delegate { Close(); };
            Controls.Add(_glyphClose);

            _bodyLabel = new Label();
            _bodyLabel.Text =
                "An installation was found on this computer." +
                "\r\nReinstall it to update or repair, or uninstall to remove the app, its certificate and the Apps-list entry.";
            _bodyLabel.Font = _bodyFont;
            _bodyLabel.ForeColor = _theme.DimText;
            _bodyLabel.BackColor = _theme.WindowBg;
            _bodyLabel.TextAlign = ContentAlignment.MiddleLeft;
            _bodyLabel.SetBounds(S(26), S(116), S(410), S(62));
            Controls.Add(_bodyLabel);

            _updateStatusLabel = new Label();
            _updateStatusLabel.Text = "Checking for updates ...";
            _updateStatusLabel.Font = _subFont;
            _updateStatusLabel.ForeColor = _theme.DimText;
            _updateStatusLabel.BackColor = _theme.WindowBg;
            _updateStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _updateStatusLabel.SetBounds(S(26), S(182), S(410), S(18));
            Controls.Add(_updateStatusLabel);

            _updateBtn = new FlatButton(_theme.Accent, _theme.AccentHover, _theme.AccentDown, _theme.ButtonText);
            _updateBtn.Text = "Update now";
            _updateBtn.Font = _bodyFont;
            _updateBtn.SetBounds(S(90), S(DesignH - 24 - 34), S(108), S(34));
            _updateBtn.Visible = false;
            _updateBtn.Click += delegate
            {
                UpdateInfo info = _updateInfo;
                if (info == null) { return; }
                Hide();
                new UpdateForm(info, true).ShowDialog();
                Close();
            };
            Controls.Add(_updateBtn);

            _reinstallBtn = new FlatButton(_theme.Accent, _theme.AccentHover, _theme.AccentDown, _theme.ButtonText);
            _reinstallBtn.Text = "Reinstall";
            _reinstallBtn.Font = _bodyFont;
            _reinstallBtn.SetBounds(S(DesignW - 26 - 108 - 10 - 108), S(DesignH - 24 - 34), S(108), S(34));
            _reinstallBtn.Click += delegate { Entry.RelaunchElevated(string.Empty); Close(); };
            Controls.Add(_reinstallBtn);

            _uninstallBtn = new FlatButton(_theme.NeutralFill, _theme.NeutralHover, _theme.NeutralDown, _theme.Foreground);
            _uninstallBtn.Text = "Uninstall";
            _uninstallBtn.Font = _bodyFont;
            _uninstallBtn.SetBounds(S(DesignW - 26 - 108), S(DesignH - 24 - 34), S(108), S(34));
            _uninstallBtn.Click += delegate { Entry.RelaunchElevated("/uninstall"); Close(); };
            Controls.Add(_uninstallBtn);

            Invalidate();
        }

        // ---- Update check (background; the chooser stays usable meanwhile) ----

        private void StartUpdateCheck()
        {
            Thread worker = new Thread(new ThreadStart(RunUpdateCheck));
            worker.IsBackground = true;
            worker.TrySetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void PostStatus(string text, Color color)
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _updateStatusLabel.ForeColor = color;
                    _updateStatusLabel.Text = text;
                });
            }
            catch (Exception)
            {
            }
        }

        private void RunUpdateCheck()
        {
            if (UpdateState.RecentlyChecked())
            {
                PostStatus("Update check skipped (checked recently).", _theme.DimText);
                return;
            }
            string error;
            UpdateInfo info = UpdateClient.TryCheck(out error);
            UpdateState.MarkCheckTime();

            if (info == null)
            {
                PostStatus("Could not reach GitHub to check for updates.", _theme.DimText);
                return;
            }
            string baseVersion = Entry.InstalledVersion;
            if (string.IsNullOrEmpty(baseVersion))
            {
                baseVersion = Entry.VersionText;
            }
            if (VersionHelper.Compare(info.Version, baseVersion) <= 0)
            {
                PostStatus("You are up to date  (v" + baseVersion + ").", _theme.DimText);
                return;
            }
            _updateInfo = info;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _updateBtn.Visible = true;
                    _updateStatusLabel.ForeColor = _theme.Foreground;
                    _updateStatusLabel.Text = "Version " + info.Version + " is available.";
                });
            }
            catch (Exception)
            {
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (Pen pen = new Pen(_theme.Outline, 1f))
            using (GraphicsPath border = UiPaint.RoundRect(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f, 8f))
            {
                g.DrawPath(pen, border);
            }

            // App badge (real logo with painted "F" fallback).
            float bx = S(26);
            float by = S(54);
            float bd = S(46);
            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_logo, bx, by, bd, bd);
            }
            else
            {
                using (GraphicsPath badge = UiPaint.RoundRect(bx, by, bd, bd, S(12)))
                using (SolidBrush fill = new SolidBrush(_theme.Accent))
                {
                    g.FillPath(fill, badge);
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush glyph = new SolidBrush(Color.White))
                    {
                        g.DrawString("F", _badgeFont, glyph, new RectangleF(bx, by - S(1), bd, bd), sf);
                    }
                }
            }

            // Title + subtitle (shows the version actually installed, when known).
            string version = Entry.InstalledVersion;
            if (string.IsNullOrEmpty(version))
            {
                version = Entry.VersionText;
            }
            if (version.Length > 20)
            {
                version = version.Substring(0, 20) + "...";
            }

            using (SolidBrush fg = new SolidBrush(_theme.Foreground))
            {
                g.DrawString("Modify FluentFlyout", _titleFont, fg, S(84), S(56));
            }
            using (SolidBrush dim = new SolidBrush(_theme.DimText))
            {
                g.DrawString("Version " + version + "  |  Already installed", _subFont, dim, S(86), S(88));
            }
        }
    }

    // ================= Update window =================

    // Mode A (UpdateInfo): a check found a newer release; prompts first, then
    // downloads and applies without UAC when the certificate is already trusted.
    // Mode B (paths): elevated continuation after a rotated certificate update;
    // trusts the new cert, installs, refreshes the uninstall entry, relaunches.
    internal sealed class UpdateForm : Form
    {
        private const int DesignW = 460;
        private const int DesignH = 276;
        private const int DragStripHeight = 46;

        private UiTheme _theme;
        private UpdateInfo _info;              // prompt-mode payload (null in apply mode)
        private string _applyBundlePath;       // apply-mode paths
        private string _applyCertPath;
        private string _applyVersion;
        private bool _startNow;
        private float _scale = 1f;

        private GlyphButton _glyphClose;
        private StatusIcon _icon;
        private Label _statusLabel;
        private Label _pctLabel;
        private MarqueeBar _bar;
        private FlatButton _updateBtn;
        private FlatButton _notNowBtn;
        private FlatButton _closeBtn;

        private Font _titleFont;
        private Font _bodyFont;
        private Font _subFont;
        private Font _badgeFont;

        private Image _logo;
        private MemoryStream _logoStream;

        private bool _workStarted;
        private bool _workFinished;
        private long _lastProgressPost;
        private WinFormsTimer _autoClose;

        internal UpdateForm(UpdateInfo info)
            : this(info, false)
        {
        }

        internal UpdateForm(UpdateInfo info, bool startNow)
        {
            _info = info;
            _startNow = startNow;
            _theme = Theme.Current;
            CommonCtor();
        }

        internal UpdateForm(string bundlePath, string certPath, string version)
        {
            _applyBundlePath = bundlePath;
            _applyCertPath = certPath;
            _applyVersion = version;
            _startNow = true;
            _theme = Theme.Current;
            CommonCtor();
        }

        private void CommonCtor()
        {
            Text = "FluentFlyout Sideloader Update";
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            BackColor = _theme.WindowBg;
            ForeColor = _theme.Foreground;
            ClientSize = new Size(DesignW, DesignH);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Load += delegate
            {
                MeasureScale();
                BuildLayout();
                if (_startNow)
                {
                    StartUpdateWork();
                }
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
            }
            catch (Exception)
            {
            }
            StartFadeIn();
        }

        private void StartFadeIn()
        {
            Opacity = 0.0;
            double step = 0;
            WinFormsTimer timer = new WinFormsTimer();
            timer.Interval = 15;
            timer.Tick += delegate
            {
                step += 1.0;
                double a = step / 10.0;
                if (a >= 1.0)
                {
                    Opacity = 1.0;
                    timer.Stop();
                    timer.Dispose();
                }
                else
                {
                    Opacity = a;
                }
            };
            timer.Start();
        }

        private void MeasureScale()
        {
            _scale = 1f;
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    float dpi = g.DpiY;
                    if (dpi > 96f) { _scale = dpi / 96f; }
                }
            }
            catch (Exception)
            {
            }
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084;
            const int HTCLIENT = 1;
            const int HTCAPTION = 2;
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                try
                {
                    if ((int)m.Result == HTCLIENT)
                    {
                        Point p = PointToClient(Cursor.Position);
                        if (p.Y < S(DragStripHeight) && !_glyphClose.Bounds.Contains(p))
                        {
                            m.Result = (IntPtr)HTCAPTION;
                        }
                    }
                }
                catch (Exception)
                {
                }
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (Pen pen = new Pen(_theme.Outline, 1f))
            using (GraphicsPath border = UiPaint.RoundRect(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f, 8f))
            {
                g.DrawPath(pen, border);
            }

            // App badge (real logo with painted "F" fallback).
            float bx = S(26);
            float by = S(54);
            float bd = S(46);
            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_logo, bx, by, bd, bd);
            }
            else
            {
                using (GraphicsPath badge = UiPaint.RoundRect(bx, by, bd, bd, S(12)))
                using (SolidBrush fill = new SolidBrush(_theme.Accent))
                {
                    g.FillPath(fill, badge);
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush glyph = new SolidBrush(Color.White))
                    {
                        g.DrawString("F", _badgeFont, glyph, new RectangleF(bx, by - S(1), bd, bd), sf);
                    }
                }
            }

            // Title + subtitle.
            string subtitle;
            if (_applyBundlePath != null)
            {
                subtitle = "Applying the update with administrator rights";
            }
            else
            {
                string baseVersion = Entry.InstalledVersion;
                if (string.IsNullOrEmpty(baseVersion))
                {
                    baseVersion = Entry.VersionText;
                }
                subtitle = "Installed: " + baseVersion + "   |   Latest: " +
                    (_info != null ? _info.Version : "?");
            }

            using (SolidBrush fg = new SolidBrush(_theme.Foreground))
            {
                g.DrawString("Update FluentFlyout", _titleFont, fg, S(84), S(56));
            }
            using (SolidBrush dim = new SolidBrush(_theme.DimText))
            {
                g.DrawString(subtitle, _subFont, dim, S(86), S(88));
            }
        }

        private void BuildLayout()
        {
            ClientSize = new Size(S(DesignW), S(DesignH));

            _titleFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Point);
            _bodyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _subFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            _badgeFont = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point);

            try
            {
                _logoStream = new MemoryStream(Entry.EmbeddedResource(Entry.ResLogo));
                _logo = Image.FromStream(_logoStream);
                _logoStream.Seek(0, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                _logo = null;
                _logoStream = null;
            }

            _glyphClose = new GlyphButton();
            _glyphClose.SetBounds(S(DesignW - 48), S(2), S(44), S(30));
            _glyphClose.Click += delegate { OnCloseRequested(); };
            Controls.Add(_glyphClose);

            _icon = new StatusIcon();
            _icon.State = StatusIcon.StateNone;
            _icon.Visible = false;
            _icon.SetBounds(S(24), S(120), S(24), S(24));
            _icon.BackColor = _theme.WindowBg;
            Controls.Add(_icon);

            _statusLabel = new Label();
            _statusLabel.Font = _bodyFont;
            _statusLabel.ForeColor = _theme.Foreground;
            _statusLabel.BackColor = _theme.WindowBg;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            if (_info != null)
            {
                string baseVersion = Entry.InstalledVersion;
                if (string.IsNullOrEmpty(baseVersion))
                {
                    baseVersion = Entry.VersionText;
                }
                _statusLabel.Text =
                    "Version " + _info.Version + " is available." +
                    "\r\nYou have " + baseVersion + " - install the new release now?";
            }
            else
            {
                _statusLabel.Text = "Preparing the update...";
            }
            _statusLabel.SetBounds(S(26), S(116), S(410), S(46));
            Controls.Add(_statusLabel);

            _pctLabel = new Label();
            _pctLabel.Font = _subFont;
            _pctLabel.ForeColor = _theme.DimText;
            _pctLabel.BackColor = _theme.WindowBg;
            _pctLabel.TextAlign = ContentAlignment.MiddleLeft;
            _pctLabel.Visible = false;
            _pctLabel.SetBounds(S(26), S(170), S(410), S(18));
            Controls.Add(_pctLabel);

            _bar = new MarqueeBar();
            _bar.Visible = false;
            _bar.SetBounds(S(26), S(196), S(410), S(10));
            _bar.BackColor = _theme.WindowBg;
            Controls.Add(_bar);

            _updateBtn = new FlatButton(_theme.Accent, _theme.AccentHover, _theme.AccentDown, _theme.ButtonText);
            _updateBtn.Text = "Update now";
            _updateBtn.Font = _bodyFont;
            _updateBtn.SetBounds(S(DesignW - 26 - 108), S(DesignH - 24 - 34), S(108), S(34));
            _updateBtn.Visible = _info != null && !_startNow;
            _updateBtn.Click += delegate { StartUpdateWork(); };
            Controls.Add(_updateBtn);

            _notNowBtn = new FlatButton(_theme.NeutralFill, _theme.NeutralHover, _theme.NeutralDown, _theme.Foreground);
            _notNowBtn.Text = "Not now";
            _notNowBtn.Font = _bodyFont;
            _notNowBtn.SetBounds(S(DesignW - 26 - 108 - 10 - 108), S(DesignH - 24 - 34), S(108), S(34));
            _notNowBtn.Visible = _info != null && !_startNow;
            _notNowBtn.Click += delegate { Close(); };
            Controls.Add(_notNowBtn);

            _closeBtn = new FlatButton(_theme.Accent, _theme.AccentHover, _theme.AccentDown, _theme.ButtonText);
            _closeBtn.Text = "Close";
            _closeBtn.Font = _bodyFont;
            _closeBtn.SetBounds(S(DesignW - 26 - 108), S(DesignH - 24 - 34), S(108), S(34));
            _closeBtn.Visible = false;
            _closeBtn.Click += delegate { Close(); };
            Controls.Add(_closeBtn);

            Invalidate();
        }

        // ---- Worker plumbing ----

        private void PostUI(MethodInvoker action)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }

        private void SetStatus(string text)
        {
            PostUI(delegate { _statusLabel.Text = text; });
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MB";
            }
            double mb = bytes / 1024.0 / 1024.0;
            return mb.ToString("0.0") + " MB";
        }

        private static string Truncate(string text, int max)
        {
            if (text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max) + "\r\n\r\n... (output truncated)";
        }

        private void StartUpdateWork()
        {
            if (_workStarted || _workFinished)
            {
                return;
            }
            _workStarted = true;

            _updateBtn.Visible = false;
            _notNowBtn.Visible = false;
            _bar.Visible = true;
            _bar.SetDeterminate(0.0);
            _pctLabel.Visible = true;
            _pctLabel.Text = string.Empty;
            _statusLabel.Text = "Starting...";

            Thread worker = new Thread(new ThreadStart(RunUpdateWork));
            worker.IsBackground = true;
            worker.TrySetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void RunUpdateWork()
        {
            if (_applyBundlePath != null)
            {
                RunApplyWork();
                return;
            }
            try
            {
                try { Directory.CreateDirectory(UpdateState.DownloadsDir); }
                catch (Exception) { }
                string zipPath = Path.Combine(UpdateState.DownloadsDir, "update.zip");
                string bundlePath = Path.Combine(UpdateState.DownloadsDir, "update.msixbundle");
                string certPath = Path.Combine(UpdateState.DownloadsDir, "update.cer");
                try { File.Delete(zipPath); } catch (Exception) { }
                try { File.Delete(bundlePath); } catch (Exception) { }
                try { File.Delete(certPath); } catch (Exception) { }

                SetStatus("Step 1 of 4: downloading the latest installer package (~91 MB)...");
                string finalUrl = UpdateClient.Download(UpdateClient.DownloadUrl, zipPath, delegate(long done, long total)
                {
                    bool force = done >= total && total > 0;
                    long now = Environment.TickCount;
                    if (!force && now - _lastProgressPost < 120)
                    {
                        return;
                    }
                    _lastProgressPost = now;
                    double frac = total > 0 ? (double)done / (double)total : 0.0;
                    PostUI(delegate
                    {
                        _bar.SetDeterminate(frac);
                        _pctLabel.Text = FormatBytes(done) + " of " + FormatBytes(total) +
                            "  (" + (int)Math.Round(frac * 100.0) + "%)";
                    });
                });

                // The website decides what we can actually install.
                string zipVersion = UpdateClient.VersionFromFileName(finalUrl);
                string baseVersion = Entry.InstalledVersion;
                if (string.IsNullOrEmpty(baseVersion))
                {
                    baseVersion = Entry.VersionText;
                }
                string effectiveVersion = zipVersion != null ? zipVersion : _info.Version;
                if (VersionHelper.Compare(effectiveVersion, baseVersion) <= 0)
                {
                    MessageBox.Show(
                        "The download currently provides version " + effectiveVersion +
                        ", which does not beat the installed " + baseVersion +
                        ".\r\n\r\nThe update feed may still be catching up - nothing was changed.",
                        "FluentFlyout Sideloader Update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    PostUI(delegate { Close(); });
                    return;
                }

                SetStatus("Step 2 of 4: unpacking the update (v" + effectiveVersion + ")...");
                UpdateClient.ExtractUpdateFiles(zipPath, UpdateState.DownloadsDir, out bundlePath, out certPath);

                SetStatus("Step 3 of 4: verifying the release certificate...");
                bool trusted;
                using (X509Certificate2 cert = new X509Certificate2(File.ReadAllBytes(certPath)))
                {
                    trusted = Operations.IsCertTrusted(cert);
                }

                if (!trusted && Entry.IsElevated())
                {
                    SetStatus("Trusting the updated release certificate...");
                    using (X509Certificate2 cert = new X509Certificate2(File.ReadAllBytes(certPath)))
                    {
                        Operations.TrustCert(cert);
                    }
                    trusted = true;
                }

                if (trusted)
                {
                    ApplyLocal(bundlePath, certPath, effectiveVersion);
                    return;
                }

                // New signing certificate for this release: trust it once via UAC.
                SetStatus("This release uses a new certificate." +
                    "\r\nContinuing with administrator approval...");
                Thread.Sleep(1500);
                Entry.RelaunchElevated("/check /apply \"" + bundlePath + "\" \"" + certPath + "\" " + _info.Version);
                PostUI(delegate { Close(); });
            }
            catch (Exception ex)
            {
                Fail(ex.Message, "The update could not be downloaded or applied.");
            }
        }

        private void ApplyLocal(string bundlePath, string certPath, string version)
        {
            SetStatus("Step 3 of 4: installing the update (this can take up to a minute)...");
            PostUI(delegate
            {
                _bar.SetIndeterminate();
                _pctLabel.Visible = false;
            });

            PowerShellResult result = PowerShellRunner.Run(Operations.InstallScript(bundlePath));
            if (result.ExitCode != 0)
            {
                Fail(result.Output, "The update could not be installed.");
                return;
            }

            SetStatus("Step 4 of 4: finishing...");
            UpdateState.SaveCert(certPath);
            long sizeKb = 0;
            try { sizeKb = new FileInfo(bundlePath).Length / 1024L; } catch (Exception) { }
            Operations.RefreshUninstallRegistry(version, sizeKb);
            PowerShellRunner.Run(Operations.LaunchScript());

            Done("FluentFlyout was updated to version " + version + "." +
                "\r\nIt is running now - look for the tray icon.");
        }

        private void RunApplyWork()
        {
            try
            {
                if (!Entry.IsAppInstalled() && Entry.StorePackageInstalled())
                {
                    Fail(
                        "The Microsoft Store version is installed. This tool only updates the GitHub sideload.",
                        "The update was not applied.");
                    return;
                }

                SetStatus("Step 1 of 3: trusting the update certificate...");
                using (X509Certificate2 cert = new X509Certificate2(File.ReadAllBytes(_applyCertPath)))
                {
                    Operations.TrustCert(cert);
                }

                SetStatus("Step 2 of 3: installing the update (this can take up to a minute)...");
                PostUI(delegate
                {
                    _bar.Visible = true;
                    _bar.SetIndeterminate();
                    _pctLabel.Visible = false;
                });

                PowerShellResult result = PowerShellRunner.Run(Operations.InstallScript(_applyBundlePath));
                if (result.ExitCode != 0)
                {
                    Fail(result.Output, "The update could not be installed.");
                    return;
                }

                SetStatus("Step 3 of 3: finishing...");
                UpdateState.SaveCert(_applyCertPath);
                long sizeKb = 0;
                try { sizeKb = new FileInfo(_applyBundlePath).Length / 1024L; } catch (Exception) { }
                Operations.RefreshUninstallRegistry(_applyVersion, sizeKb);
                PowerShellRunner.Run(Operations.LaunchScript());

                string versionText = string.IsNullOrEmpty(_applyVersion) ? "" : " to version " + _applyVersion;
                Done("FluentFlyout was updated" + versionText + "." +
                    "\r\nIt is running now - look for the tray icon.");
            }
            catch (Exception ex)
            {
                Fail(ex.Message, "The update could not be applied.");
            }
        }

        private void Done(string text)
        {
            _workFinished = true;
            PostUI(delegate
            {
                _bar.Visible = false;
                _pctLabel.Visible = false;
                _icon.State = StatusIcon.StateSuccess;
                _icon.Visible = true;
                _statusLabel.SetBounds(S(56), S(118), S(382), S(46));
                _statusLabel.ForeColor = _theme.Foreground;
                _statusLabel.Text = text;
                _closeBtn.Visible = true;
            });
            SetupAutoClose(6000);
        }

        private void Fail(string details, string message)
        {
            _workFinished = true;
            PostUI(delegate
            {
                _bar.Visible = false;
                _pctLabel.Visible = false;
                _icon.State = StatusIcon.StateError;
                _icon.Visible = true;
                _statusLabel.SetBounds(S(56), S(118), S(382), S(46));
                _statusLabel.ForeColor = _theme.Danger;
                _statusLabel.Text = message + "\r\nSee the error dialog for details.";
                _closeBtn.Visible = true;
            });
            MessageBox.Show(
                Truncate(details == null ? "" : details.Trim(), 1800),
                "FluentFlyout Sideloader Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void SetupAutoClose(int intervalMs)
        {
            if (_autoClose != null)
            {
                return;
            }
            _autoClose = new WinFormsTimer();
            _autoClose.Interval = intervalMs;
            _autoClose.Tick += delegate
            {
                _autoClose.Stop();
                if (_workFinished)
                {
                    Close();
                }
            };
            _autoClose.Start();
        }

        private void OnCloseRequested()
        {
            if (_workStarted && !_workFinished)
            {
                DialogResult dr = MessageBox.Show(
                    this,
                    "The update is still in progress. Quit and cancel?",
                    "FluentFlyout Sideloader Update",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes)
                {
                    return;
                }
                PowerShellRunner.KillCurrent();
                Environment.Exit(2);
            }
            Close();
        }
    }
}
