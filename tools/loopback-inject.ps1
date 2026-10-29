# 无手机时的回环注入脚本（PC 单端自测）
# 用法：先让 PC 进入 Wi-Fi 模式，然后在 PowerShell 7 中执行本脚本。
# 参见 docs/01-architecture.md 附录 A。

$ErrorActionPreference = 'Stop'
$script:Udp = [Net.Sockets.UdpClient]::new()

function Send-BasPacket {
    param(
        [byte]$Type,
        [uint32]$Seq,
        [uint32]$Ts,
        [byte[]]$Payload = [byte[]]@(),
        [int]$Port
    )
    $p = New-Object byte[] (16 + $Payload.Length)
    [Text.Encoding]::ASCII.GetBytes('BAS1').CopyTo($p, 0)
    $p[4] = $Type
    [BitConverter]::GetBytes([uint32]$Seq).CopyTo($p, 8)
    [BitConverter]::GetBytes([uint32]$Ts).CopyTo($p, 12)
    if ($Payload.Length -gt 0) { $Payload.CopyTo($p, 16) }
    [void]$script:Udp.Send($p, $p.Length, '127.0.0.1', $Port)
    Write-Host ("sent type={0} seq={1} len={2} -> {3}" -f $Type, $Seq, $p.Length, $Port)
}

Write-Host '1) 发送 START'
Send-BasPacket -Type 2 -Seq 0 -Ts 1000000 -Port 45678

Write-Host '2) 发送 10 个 5ms AUDIO 包（seq 0..4, 12..16，制造 7 包缺口）'
$audio = [byte[]]::new(960)
foreach ($i in 0..4) {
    Send-BasPacket -Type 1 -Seq ([uint32]$i) -Ts ([uint32](1000000 + $i * 5000)) -Payload $audio -Port 45679
}
foreach ($i in 12..16) {
    Send-BasPacket -Type 1 -Seq ([uint32]$i) -Ts ([uint32](1000000 + $i * 5000)) -Payload $audio -Port 45679
}

Start-Sleep -Milliseconds 300

Write-Host '3) 发送非法 magic 包（应计 malformed，不崩溃）'
$bad = [byte[]]::new(16)
[Text.Encoding]::ASCII.GetBytes('XXXX').CopyTo($bad, 0)
$bad[4] = 1
[void]$script:Udp.Send($bad, $bad.Length, '127.0.0.1', 45679)

Write-Host '4) 发送 STOP'
Send-BasPacket -Type 3 -Seq 0 -Ts 1100000 -Port 45678

$script:Udp.Dispose()
Write-Host ''
Write-Host '预期：日志出现 Wifi.Start / Wifi.FirstAudio / Wifi.Playing / Audio.Gap(missing=7) / Wifi.Stop / Wifi.DrainDone'
