$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('issue8-analyzer-'+[Guid]::NewGuid()+'.jsonl')
try {
    $events=@(
        @{event='TX';command='0x08';relative_ms=100;packet_hex='08 01 00 00 00 00 00 00 00 00 00 00 00 00 00 09'},
        @{event='native_disconnect';relative_ms=200},
        # Old captures mislabeled this cached notification as an advertisement.
        @{event='advertisement_seen';relative_ms=300;note='RSSI=-127; native_timestamp=fixture'},
        @{event='advertisement_out_of_range';relative_ms=400;note='RSSI=-127; cached last advertisement'},
        @{event='advertisement_seen';relative_ms=500;note='RSSI=-60; native_timestamp=fixture'},
        @{event='intervention_requested';relative_ms=600;note='charger-attach'}
    )
    $events | ForEach-Object {$_ | ConvertTo-Json -Compress} | Set-Content -LiteralPath $fixture
    $result=(& (Join-Path $PSScriptRoot 'Analyze.ps1') -InputPath $fixture) | ConvertFrom-Json
    if($result.target_advertisements -ne 1 -or $result.out_of_range_notifications -ne 2) {throw 'Historical/current sentinel classification failed.'}
    if($result.command08.post_command_advertisements -ne 1 -or $result.command08.first_post_command_ad_ms -ne 400) {throw 'Actual advertisement latency failed.'}
    if($result.command08.native_disconnect_after_tx_ms -ne 100 -or $result.command08.post_command_out_of_range_notifications -ne 2) {throw 'Post-command evidence failed.'}
    if($result.interventions.Count -ne 0 -or $result.requested_interventions[0] -ne 'charger-attach') {throw 'Requested action must not be reported as completed.'}
    Write-Output 'Analyzer regression PASS: legacy/new sentinel, actual advertisement latency, requested/completed intervention distinction.'
}
finally {Remove-Item -LiteralPath $fixture -ErrorAction SilentlyContinue}
