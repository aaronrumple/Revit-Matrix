using System.Windows;

namespace RevitJournalViewer;

/// <summary>
/// Windows notifications (shown as toasts on Windows 10/11) through a tray icon that exists
/// only while the app runs. Clicking a notification brings the app to the front.
/// </summary>
public sealed class Notifier : IDisposable
{
    private System.Windows.Forms.NotifyIcon? _icon;
    private readonly Window _owner;

    public Notifier(Window owner) => _owner = owner;

    public void Show(string title, string text)
    {
        try
        {
            if (_icon == null)
            {
                _icon = new System.Windows.Forms.NotifyIcon { Text = "The Revit Matrix", Visible = true };
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TheRevitMatrix.ico"));
                if (res != null) _icon.Icon = new System.Drawing.Icon(res.Stream);
                _icon.BalloonTipClicked += (_, _) => BringOwnerForward();
                _icon.Click += (_, _) => BringOwnerForward();
            }
            if (text.Length > 200) text = text[..200] + "…";
            _icon.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.None);
        }
        catch
        {
            // notifications are a convenience; never fail over them
        }
    }

    private void BringOwnerForward()
    {
        _owner.Dispatcher.InvokeAsync(() =>
        {
            if (_owner.WindowState == WindowState.Minimized) _owner.WindowState = WindowState.Normal;
            _owner.Activate();
        });
    }

    public void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;   // otherwise a dead icon lingers in the tray
        _icon.Dispose();
        _icon = null;
    }
}
