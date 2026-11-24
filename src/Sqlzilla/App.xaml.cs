using Microsoft.UI.Xaml;
using Sqlzilla.Services;

namespace Sqlzilla;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        // Interactive Entra sign-in (WAM broker) needs a parent window handle.
        ConnectionFactory.RegisterEntraProvider(() => WinRT.Interop.WindowNative.GetWindowHandle(window));
        window.Activate();
        var dispatcher = window.DispatcherQueue;
        Program.ListenForRedirects(files => dispatcher.TryEnqueue(() => window.OpenFiles(files, bringToFront: true)));
        window.OpenFiles(Program.FilesFrom(Environment.GetCommandLineArgs().Skip(1)), bringToFront: false);
    }
}
