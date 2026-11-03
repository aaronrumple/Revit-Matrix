using System.Windows;
using System.Windows.Threading;

namespace RevitJournalViewer;

public partial class App : Application
{
    /// <summary>Keep the splash up at least this long so it doesn't just flash.</summary>
    private static readonly TimeSpan MinSplashTime = TimeSpan.FromSeconds(1.8);

    /// <summary>"v1.0.0" – from the assembly version (set by &lt;Version&gt; or -p:Version=).</summary>
    public static string VersionLabel
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var started = DateTime.UtcNow;

        var splash = new SplashWindow();
        splash.Show();
        await Dispatcher.Yield(DispatcherPriority.Background); // let the splash paint first

        splash.SetStatus("Looking for Revit sessions…");
        var main = new MainWindow();
        MainWindow = main;   // the splash was created first; make sure closing main exits the app

        var remaining = MinSplashTime - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

        main.Show();
        main.Activate();
        splash.FadeOutAndClose();
    }
}
