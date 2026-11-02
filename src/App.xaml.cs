using System.Threading;
using System.Windows;

namespace SnipClone;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _activate;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool snipNow = e.Args.Contains("--snip", StringComparer.OrdinalIgnoreCase);

        // One instance only: a second launch asks the running one to come forward (or start a snip).
        _single = new Mutex(true, @"Local\SnipCloneSingleInstance", out bool first);
        string eventName = @"Local\SnipCloneActivate" + (snipNow ? "Snip" : "");
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var other)) other.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow(snipNow);
        MainWindow = window;
        window.Show();

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnipCloneActivate");
        var snipEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SnipCloneActivateSnip");
        var thread = new Thread(() =>
        {
            var handles = new WaitHandle[] { _activate, snipEvent };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                Dispatcher.BeginInvoke(() =>
                {
                    if (which == 1) window.StartSnip();
                    else window.BringToFront();
                });
            }
        }) { IsBackground = true };
        thread.Start();
    }
}
