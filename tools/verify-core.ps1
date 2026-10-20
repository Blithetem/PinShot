# PinShot 命令行核心功能回归
# 用法：pwsh -NoProfile -File tools\verify-core.ps1
# 全部通过程序内置自检 / CLI 入口驱动，不模拟鼠标键盘，不向其他窗口发送输入。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\PinShot.App\bin\Release\net8.0-windows10.0.19041.0\PinShot.exe'
$infoLog = Join-Path $env:LOCALAPPDATA 'PinShot\logs\info.log'
$errorLog = Join-Path $env:LOCALAPPDATA 'PinShot\logs\error.log'
$tmp = [System.IO.Path]::GetTempPath()

if (-not (Test-Path $exe)) {
    Write-Host "找不到可执行文件：$exe" -ForegroundColor Red
    exit 1
}

function Get-LogCount {
    $count = 0
    if (Test-Path $infoLog) { $count += (Get-Content $infoLog -Encoding UTF8).Count }
    if (Test-Path $errorLog) { $count += (Get-Content $errorLog -Encoding UTF8).Count }
    return $count
}

function Get-NewLogText {
    param([int]$InfoBefore, [int]$ErrorBefore)

    $parts = @()
    if (Test-Path $infoLog) {
        $all = Get-Content $infoLog -Encoding UTF8
        if ($all.Count -gt $InfoBefore) { $parts += $all[$InfoBefore..($all.Count - 1)] }
    }
    if (Test-Path $errorLog) {
        $all = Get-Content $errorLog -Encoding UTF8
        if ($all.Count -gt $ErrorBefore) { $parts += $all[$ErrorBefore..($all.Count - 1)] }
    }
    return ($parts -join "`n")
}

$fullShot = Join-Path $tmp 'pinshot-verify-full.png'
$regionShot = Join-Path $tmp 'pinshot-verify-region.png'

$tests = @(
    @{ Name = 'CLI 全屏截图'; Args = @('--capture-full', '--output', $fullShot); Timeout = 40; File = $fullShot },
    @{ Name = 'CLI 选区截图'; Args = @('--capture-region', '--region', '100,100,800,600', '--output', $regionShot); Timeout = 40; File = $regionShot },
    @{ Name = '截图装饰'; Args = @('--decorate-test'); Timeout = 40; Expect = '装饰自检' },
    @{ Name = '贴图管理'; Args = @('--pin-manager-test'); Timeout = 90; Expect = 'RESULT PASS' },
    @{ Name = '长图编辑器'; Args = @('--editor-test'); Timeout = 40; Expect = '未裁剪=True' },
    @{ Name = '长截图拼接'; Args = @('--longshot-test'); Timeout = 90; Expect = '长截图自检' },
    @{ Name = 'GIF 录制'; Args = @('--gif-test'); Timeout = 90; Expect = 'GIF 自检' },
    @{ Name = '屏幕录制'; Args = @('--record-test'); Timeout = 90; Expect = '录制成功' },
    @{ Name = 'OCR 识别'; Args = @('--ocr-test'); Timeout = 90; Expect = 'OCR 自检' }
)

$results = @()

foreach ($test in $tests) {
    Get-Process PinShot -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 250

    if ($test.File -and (Test-Path $test.File)) { Remove-Item $test.File -Force }

    $infoBefore = 0
    if (Test-Path $infoLog) { $infoBefore = (Get-Content $infoLog -Encoding UTF8).Count }
    $errorBefore = 0
    if (Test-Path $errorLog) { $errorBefore = (Get-Content $errorLog -Encoding UTF8).Count }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exe -ArgumentList $test.Args -PassThru -WindowStyle Hidden
    $exited = $process.WaitForExit($test.Timeout * 1000)
    if (-not $exited) {
        Get-Process PinShot -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    $sw.Stop()
    Start-Sleep -Milliseconds 250

    $logText = Get-NewLogText -InfoBefore $infoBefore -ErrorBefore $errorBefore

    if ($test.File) {
        if (Test-Path $test.File) {
            $size = (Get-Item $test.File).Length
            $status = if ($size -gt 1000) { 'PASS' } else { 'FAIL' }
            $detail = "产物 $size bytes"
        } else {
            $status = 'FAIL'
            $detail = '未生成产物'
        }
    } else {
        $status = if ($logText -match $test.Expect) { 'PASS' } else { 'FAIL' }
        $detail = ($logText -split "`n" | Where-Object { $_ -match $test.Expect } | Select-Object -Last 1)
        if (-not $detail) {
            $detail = ($logText -split "`n" | Select-Object -Last 1)
        }
    }

    $results += [pscustomobject]@{
        Status  = $status
        Name    = $test.Name
        Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        Detail  = "$detail"
    }
}

Get-Process PinShot -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host ''
Write-Host '================ PinShot 核心功能回归 ================' -ForegroundColor Cyan

foreach ($row in $results) {
    $color = if ($row.Status -eq 'PASS') { 'Green' } else { 'Red' }
    Write-Host ("{0,-4} {1,-14} {2,5}s  {3}" -f $row.Status, $row.Name, $row.Seconds, $row.Detail) -ForegroundColor $color
}

$passCount = ($results | Where-Object Status -eq 'PASS').Count
$failCount = $results.Count - $passCount
Write-Host '------------------------------------------------------'
Write-Host ("合计：PASS {0} / FAIL {1}" -f $passCount, $failCount) -ForegroundColor $(if ($failCount -eq 0) { 'Green' } else { 'Red' })
Write-Host ''

exit $(if ($failCount -eq 0) { 0 } else { 1 })
