using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var connection = new RingConnectionController(new ColmiRingBleService());
        _viewModel = new MainViewModel(connection, new OscOutputService(), action => Dispatcher.BeginInvoke(action));
        DataContext = _viewModel;

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += RefreshView;
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_timer.IsEnabled || _closing) return;
        _timer.Start();
        await _viewModel.InitializeAsync();
    }

    private void RefreshView(object? sender, EventArgs e)
    {
        _viewModel.Refresh();
        RedrawHeartRateGraph();
        if (BatteryHistoryToggle.IsChecked == true) RedrawBatteryHistoryGraph();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        IsEnabled = false;
        try { await _viewModel.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Application shutdown: {ex.GetType().Name}"); }
        finally
        {
            Closing -= Window_Closing;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
