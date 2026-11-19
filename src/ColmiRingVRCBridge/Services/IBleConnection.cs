using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

// One attempt owns these resources locally; only a fully initialized attempt is published.
internal interface IBleConnection : IAsyncDisposable
{
    event Action<IBleConnection, int>? HeartRateUpdated;
    event Action<IBleConnection, BatteryState>? BatteryUpdated;
    event Action<IBleConnection, bool>? ConnectionChanged;
    event Action<IBleConnection, string>? ProtocolWarning;
    bool IsConnected { get; }
    BleTelemetryDiagnostics GetDiagnostics();
    Task OpenAsync(ulong address, CancellationToken token);
    Task DiscoverServiceAsync(CancellationToken token);
    Task DiscoverCharacteristicsAsync(CancellationToken token);
    Task<RingDeviceInfo> ReadDeviceInfoAsync(CancellationToken token);
    Task EnableNotificationsAsync(CancellationToken token);
    Task StartSessionAsync(CancellationToken token);
}
