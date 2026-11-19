using System.Windows;

namespace ColmiRingVRCBridge;

public partial class App : Application
{
    private const string InstanceMutexName = "ColmiRingVRCBridge.SingleInstance";
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The named handle reserves this user/session slot until the app exits.
        var mutex = new Mutex(initiallyOwned: false, InstanceMutexName, new NamedWaitHandleOptions(), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            Shutdown();
            return;
        }

        _instanceMutex = mutex;
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { base.OnExit(e); }
        finally { _instanceMutex?.Dispose(); }
    }
}
