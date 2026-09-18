using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Photo2CullNet.App.Native;

/// <summary>
/// A native "choose a folder" dialog per platform. Photino doesn't ship one
/// itself (it's a thin webview shell, not a full desktop framework), so
/// this shells out to each OS's own picker rather than pulling in a whole
/// GUI toolkit just for one dialog. Returns null on cancel, on any failure,
/// or on a platform/tool that isn't available -- callers should keep the
/// existing manual text field as the fallback either way.
/// </summary>
public static class FolderPicker
{
    public static Task<string?> PickFolderAsync(string title = "Select a folder to scan")
    {
        if (OperatingSystem.IsWindows()) return Task.FromResult(PickFolderWindows(title));
        if (OperatingSystem.IsMacOS()) return PickFolderMacAsync(title);
        if (OperatingSystem.IsLinux()) return PickFolderLinuxAsync(title);
        return Task.FromResult<string?>(null);
    }

    // --- Windows: SHBrowseForFolder (shell32), the classic folder-tree
    // picker. Deliberately not System.Windows.Forms.FolderBrowserDialog --
    // that would force a Windows-specific target framework moniker onto an
    // otherwise single-TFM cross-platform project just for this one
    // dialog. Run on a dedicated STA thread since shell dialogs require it
    // regardless of the calling thread's apartment state. ---

    [SupportedOSPlatform("windows")]
    private static string? PickFolderWindows(string title)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ShowShellFolderBrowser(title);
            }
            catch
            {
                result = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static string? ShowShellFolderBrowser(string title)
    {
        var displayName = new StringBuilder(260);
        var bi = new BROWSEINFO
        {
            hwndOwner = IntPtr.Zero,
            pidlRoot = IntPtr.Zero,
            pszDisplayName = displayName,
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            lpfn = null,
            lParam = IntPtr.Zero,
            iImage = 0,
        };

        IntPtr pidl = SHBrowseForFolder(ref bi);
        if (pidl == IntPtr.Zero) return null;

        try
        {
            var path = new StringBuilder(260);
            return SHGetPathFromIDList(pidl, path) ? path.ToString() : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private const uint BIF_RETURNONLYFSDIRS = 0x0001;
    private const uint BIF_NEWDIALOGSTYLE = 0x0040;

    private delegate int BrowseCallbackProc(IntPtr hwnd, uint msg, IntPtr lParam, IntPtr lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public StringBuilder pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public BrowseCallbackProc? lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    // --- macOS: AppleScript's built-in "choose folder", via osascript.
    // No native binding needed for one dialog box. ---

    private static async Task<string?> PickFolderMacAsync(string title)
    {
        var script = $"POSIX path of (choose folder with prompt \"{title.Replace("\"", "'")}\")";
        var output = await RunAndCaptureAsync("osascript", ["-e", script]);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    // --- Linux: no single standard dialog API -- try the common desktop
    // tools in order, falling back to null (manual text entry) if none
    // are installed. ---

    private static async Task<string?> PickFolderLinuxAsync(string title)
    {
        foreach (var (exe, args) in new[]
        {
            ("zenity", new[] { "--file-selection", "--directory", $"--title={title}" }),
            ("kdialog", new[] { "--getexistingdirectory", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }),
        })
        {
            if (!IsOnPath(exe)) continue;
            var output = await RunAndCaptureAsync(exe, args);
            return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
        }
        return null;
    }

    private static bool IsOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, exe)));
    }

    private static async Task<string?> RunAndCaptureAsync(string fileName, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
