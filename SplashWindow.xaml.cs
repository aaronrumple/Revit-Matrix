using System.Windows;
using System.Windows.Media.Animation;

namespace RevitJournalViewer;

/// <summary>Splash shown while the main window starts (Assets/Splash.jpg).</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        VersionText.Text = App.VersionLabel;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Fade out, then close.</summary>
    public void FadeOutAndClose()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
