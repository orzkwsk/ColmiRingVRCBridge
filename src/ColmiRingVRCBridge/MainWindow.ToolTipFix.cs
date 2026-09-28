using System.Windows;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // ToolTipService-managed tooltips cannot use StaysOpen=false.
        // InitializeBatteryHistoryToolTip() runs in the constructor, so normalize it
        // before the first hover opens the battery history tooltip.
        if (_batteryHistoryToolTip is not null)
        {
            _batteryHistoryToolTip.StaysOpen = true;
        }
    }
}
