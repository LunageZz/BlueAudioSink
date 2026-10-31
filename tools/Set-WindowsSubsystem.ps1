param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
# Equivalent to editbin /SUBSYSTEM:WINDOWS, without requiring Visual Studio.
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$stream = [IO.File]::Open($resolvedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
try {
    $reader = New-Object IO.BinaryReader($stream)
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Not a Windows PE executable.' }
    $stream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    if ($peOffset -lt 0 -or $peOffset + 94 -gt $stream.Length) { throw 'Invalid PE header offset.' }
    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
    $stream.Position = $peOffset + 24
    $magic = $reader.ReadUInt16()
    if ($magic -ne 0x10B -and $magic -ne 0x20B) { throw 'Unsupported PE optional header.' }
    $stream.Position = $peOffset + 24 + 68
    $subsystem = $reader.ReadUInt16()
    if ($subsystem -ne 2 -and $subsystem -ne 3) { throw 'Unexpected PE subsystem.' }
    $stream.Position -= 2
    $writer = New-Object IO.BinaryWriter($stream)
    $writer.Write([uint16]2)
    $writer.Flush()
    Write-Output 'PE subsystem set to WINDOWS (GUI).'
}
finally { $stream.Dispose() }
