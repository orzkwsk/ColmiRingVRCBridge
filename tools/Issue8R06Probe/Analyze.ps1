param([Parameter(Mandatory)][string]$InputPath)
$ErrorActionPreference='Stop'
$events=Get-Content -LiteralPath $InputPath | ForEach-Object { $_ | ConvertFrom-Json }
# Print aggregate facts only. Raw values, serial/address, and checksums never leave input.
$trials=@($events | Where-Object event -eq 'trial_start')
$results=for($i=0;$i -lt $trials.Count;$i++) {
    $begin=$trials[$i].relative_ms
    $end=if($i+1 -lt $trials.Count){$trials[$i+1].relative_ms}else{[double]::PositiveInfinity}
    $trial=@($events | Where-Object {$_.relative_ms -ge $begin -and $_.relative_ms -lt $end})
    $start=$trial | Where-Object { $_.event -eq 'TX' -and $_.command -eq '0x69' } | Select-Object -First 1
    $hr=@($trial | Where-Object {
        if($_.event -ne 'RX') {return $false}
        $p=@($_.packet_hex -split ' ' | ForEach-Object {[Convert]::ToInt32($_,16)})
        if($p.Count -ne 16 -or (($p[0..14] | Measure-Object -Sum).Sum -band 255) -ne $p[15]) {return $false}
        $v=if($p[0] -eq 0x1e){$p[1]}elseif($p[0] -eq 0x69 -and $p[2] -eq 0){$p[3]}else{0}
        return $v -ge 30 -and $v -le 220
    })
    $statuses=@($trial | Where-Object {$_.event -eq 'RX' -and $_.command -eq '0x69'} | Group-Object {($_.packet_hex -split ' ')[2]} | ForEach-Object { @{status_byte='0x'+$_.Name;count=$_.Count} })
    [ordered]@{trial=$i+1;label=$trials[$i].label;sequence_note=$trials[$i].note;
        first_hr_after_start_ms=if($hr.Count){[math]::Round($hr[0].relative_ms-$start.relative_ms,1)}else{$null};
        hr_samples=$hr.Count;hr_span_ms=if($hr.Count){[math]::Round($hr[-1].relative_ms-$hr[0].relative_ms,1)}else{0};
        status_counts=$statuses;rx_commands=@($trial|Where-Object event -eq 'RX'|Group-Object command|ForEach-Object {@{command=$_.Name;count=$_.Count}});
        summaries=@($trial|Where-Object event -eq 'summary'|ForEach-Object note)}
}
$command=$events | Where-Object {$_.event -eq 'TX' -and $_.command -eq '0x08'} | Select-Object -First 1
$commandResult=if($command){
    $native=$events|Where-Object {$_.event -eq 'native_disconnect' -and $_.relative_ms -ge $command.relative_ms}|Select-Object -First 1
    $ads=@($events|Where-Object {$_.event -eq 'advertisement_seen' -and $_.relative_ms -ge $command.relative_ms})
    [ordered]@{tx='08 01 00 00 00 00 00 00 00 00 00 00 00 00 00 09';
        native_disconnect_after_tx_ms=if($native){[math]::Round($native.relative_ms-$command.relative_ms,1)}else{$null};
        post_command_advertisements=$ads.Count;
        first_post_command_ad_ms=if($ads.Count){[math]::Round($ads[0].relative_ms-$command.relative_ms,1)}else{$null};
        native_disconnect_events=@($events|Where-Object event -eq 'native_disconnect').Count;
        reconnect_events=@($events|Where-Object event -eq 'reconnect').Count;
        post_command_rx_commands=@($events|Where-Object {$_.event -eq 'RX' -and $_.relative_ms -gt $command.relative_ms}|Group-Object command|ForEach-Object {@{command=$_.Name;count=$_.Count}});
        post_command_status_counts=@($events|Where-Object {$_.event -eq 'RX' -and $_.command -eq '0x69' -and $_.relative_ms -gt $command.relative_ms}|Group-Object {($_.packet_hex -split ' ')[2]}|ForEach-Object {@{status_byte='0x'+$_.Name;count=$_.Count}});
        boundaries=@($events|Where-Object event -eq 'observation_boundary'|ForEach-Object note);
        post_command_protocol_tx=@($events|Where-Object {$_.event -eq 'TX' -and $_.relative_ms -gt $command.relative_ms}).Count}
}else{$null}
[ordered]@{capture=Split-Path $InputPath -Leaf;trials=@($results);command08=$commandResult;
    interventions=@($events|Where-Object event -eq 'manual_intervention'|ForEach-Object note);
    target_advertisements=@($events|Where-Object event -eq 'advertisement_seen').Count} | ConvertTo-Json -Depth 8
