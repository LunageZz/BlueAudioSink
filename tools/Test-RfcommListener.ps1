param([switch]$Hardware)

$ErrorActionPreference = 'Stop'
# Compile the actual product source; no .NET SDK or audio output is required.
Add-Type -Path (Join-Path $PSScriptRoot '../src/SinkLite/Platform/RfcommListener.cs')
$listenerType = [AppDomain]::CurrentDomain.GetAssemblies() |
    ForEach-Object { $_.GetType('SinkLite.Platform.RfcommListener', $false) } |
    Where-Object { $_ } | Select-Object -First 1
$flags = [Reflection.BindingFlags]::NonPublic
$addressType = $listenerType.GetNestedType('CsAddress', $flags)
$socketAddressType = $listenerType.GetNestedType('NativeSocketAddress', $flags)
if ($null -eq $socketAddressType) { throw 'Missing native SOCKET_ADDRESS nesting (x64 SDP registration fails with 10022).' }
$nativeAddressSize = if ([IntPtr]::Size -eq 8) { 16 } else { 8 }
$socketOffset = [Runtime.InteropServices.Marshal]::OffsetOf($addressType, 'SocketType').ToInt32()
$protocolOffset = [Runtime.InteropServices.Marshal]::OffsetOf($addressType, 'Protocol').ToInt32()
if ([Runtime.InteropServices.Marshal]::SizeOf([type]$socketAddressType) -ne $nativeAddressSize -or
    $socketOffset -ne 2 * $nativeAddressSize -or $protocolOffset -ne 2 * $nativeAddressSize + 4) {
    throw "Invalid CSADDR_INFO layout: SocketType=$socketOffset Protocol=$protocolOffset"
}
Write-Output "PASS: native CSADDR_INFO layout (SocketType=$socketOffset Protocol=$protocolOffset)."

if ($Hardware) {
    # Repeated start/dispose also checks that SDP registrations and channels release.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $listener = [Activator]::CreateInstance($listenerType, $true)
        try {
            $listenerType.GetMethod('Start').Invoke($listener, @())
            Write-Output "PASS: RFCOMM bind/listen/SDP registration ($attempt/3)."
        }
        finally { $listener.Dispose() }
    }
}
