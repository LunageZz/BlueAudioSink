# BlueAudioSink 音频流向诊断脚本
# 用法: pwsh -File tools\diagnose-flow.ps1 [-Seconds 30]
param(
    [int]$Seconds = 30
)

$ErrorActionPreference = 'Continue'

# 统一控制台编码，避免中文在使用不同代码页的终端中出现乱码。
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$root = Split-Path -Parent $PSScriptRoot

function Find-BuiltExe([string]$name) {
    $binRoot = Join-Path $root 'src\SinkLite\bin'
    $toolRoot = Join-Path $root 'tools\SinkProbe\bin'
    $paths = @()
    if (Test-Path $binRoot) { $paths += $binRoot }
    if (Test-Path $toolRoot) { $paths += $toolRoot }
    foreach ($p in $paths) {
        $hit = Get-ChildItem -Path $p -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($hit) { return $hit }
    }
    return $null
}

Write-Host '=== BlueAudioSink 音频流向诊断 ===' -ForegroundColor Cyan
Write-Host ''

# 1) 定位可执行文件
$exe = Find-BuiltExe 'BlueAudioSink.exe'

if (-not $exe) {
    Write-Host '未找到已发布的 BlueAudioSink.exe，请先运行：' -ForegroundColor Yellow
    Write-Host '  dotnet publish .\src\SinkLite\SinkLite.csproj -c Release -p:Platform=x64'
    exit 1
}
Write-Host ('可执行文件: ' + $exe.FullName)
Write-Host ('体积: ' + [math]::Round($exe.Length / 1MB, 2) + ' MB')
Write-Host ''

# 2) 三层状态诊断
Write-Host ('--- 采样 ' + $Seconds + ' 秒（请在手机上播放音乐以验证音频层）---') -ForegroundColor Cyan
& $exe.FullName --diagnose $Seconds
Write-Host ''

# 3) 音频端点概览
Write-Host '--- 全部音频端点（含未插入/禁用）---' -ForegroundColor Cyan
$probe = Find-BuiltExe 'SinkProbe.exe'

if ($probe) {
    & $probe.FullName endpoints
} else {
    Write-Host '（未找到 SinkProbe.exe，跳过）' -ForegroundColor DarkGray
}
Write-Host ''

# 4) 日志摘要
Write-Host '--- 日志摘要（最近 20 条）---' -ForegroundColor Cyan
$logDir = Join-Path $env:LOCALAPPDATA 'BlueAudioSink\logs'
if (Test-Path $logDir) {
    $log = Get-ChildItem $logDir -Filter '*.jsonl' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Write-Host ('日志文件: ' + $log.FullName)
        Get-Content $log.FullName -Tail 20 | ForEach-Object {
            try {
                $o = $_ | ConvertFrom-Json
                Write-Host ('  ' + $o.ts.Substring(11) + '  ' + $o.level.PadRight(5) + ' ' + $o.event)
            } catch {
                Write-Host ('  ' + $_)
            }
        }
    } else {
        Write-Host '（暂无日志文件）' -ForegroundColor DarkGray
    }
} else {
    Write-Host '（日志目录不存在，程序尚未运行过）' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '=== 诊断结束 ===' -ForegroundColor Cyan
Write-Host '判读要点：'
Write-Host '  状态停在 Armed     -> 控制层就绪，手机没有发音频（检查手机侧）'
Write-Host '  状态到 LinkReady   -> 链路已建立但无声音，检查输出设备是否被抢占'
Write-Host '  状态到 Flowing     -> 音频已到达电脑'
