using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Sqlzilla;

/// <summary>
/// Custom entry point so that opening a .sql file from Explorer while Sqlzilla is running
/// adds a tab to the running window instead of starting a second copy.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Sqlzilla.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (FilesFrom(args).Count > 0 && RedirectToRunningInstance()) return 0;

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Existing files among command-line arguments, as full paths.</summary>
    public static List<string> FilesFrom(IEnumerable<string> args)
    {
        var exe = Environment.ProcessPath;
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            string full;
            try { full = Path.GetFullPath(arg); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (File.Exists(full) && !string.Equals(full, exe, StringComparison.OrdinalIgnoreCase)) files.Add(full);
        }
        return files;
    }

    /// <summary>Splits a raw command line the way Windows does.</summary>
    public static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(argv); }
    }

    /// <summary>The first instance registers itself; later ones hand their activation (with its file arguments) to it.</summary>
    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent) return false;

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AllowSetForegroundWindow(main.ProcessId);
        // Redirecting must not block the STA thread, so wait on a worker.
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Task.Run(async () =>
        {
            try { await main.RedirectActivationToAsync(activation); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        return error == null;
    }

    /// <summary>Called by the main instance so it receives redirected activations.</summary>
    public static void ListenForRedirects(Action<IReadOnlyList<string>> openFiles)
    {
        var current = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!current.IsCurrent) return;   // another window already owns file activations
        current.Activated += (_, e) =>
        {
            if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
                openFiles(FilesFrom(SplitCommandLine(launch.Arguments)));
        };
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
