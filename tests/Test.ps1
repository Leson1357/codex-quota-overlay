$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$scratch = Join-Path $PSScriptRoot '.test-work'
[void](New-Item -ItemType Directory -Path $scratch -Force)
$fake = Join-Path $scratch 'FakeCodex.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe /optimize+ /codepage:65001 "/out:$fake" "/reference:$framework\System.Web.Extensions.dll" (Join-Path $PSScriptRoot 'FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
Add-Type -AssemblyName System.Windows.Forms,UIAutomationClient,UIAutomationTypes,WindowsBase
[void][Reflection.Assembly]::LoadFrom((Join-Path $project 'CodexQuota.exe'))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$overlay = [CodexQuota]::new($fake)
$overlay.GetType().GetField('timer', $flags).GetValue($overlay).Stop()
$overlay.Bounds = [Drawing.Rectangle]::new(-30000, -30000, 800, 38)
function Field($name) { $overlay.GetType().GetField($name, $flags) }
function Invoke-Private($name, $arguments = @()) {
    $overlay.GetType().GetMethod($name, $flags).Invoke($overlay, $arguments)
}
function Assert($condition, $message) { if (!$condition) { throw $message } }
function Hover($x, $y) {
    [void](Invoke-Private 'UpdateHoverVisibility' @([Drawing.Point]::new($overlay.Left + $x, $overlay.Top + $y)))
}
function Pump-Until($condition) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        [void](Invoke-Private 'Tick')
        if (& $condition) { return }
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out waiting for test protocol state. Mode={0}, Ready={1}, WaitingId={2}, Labels={3} / {4}' -f
        $env:CODEX_QUOTA_TEST_MODE, (Field 'ready').GetValue($overlay), (Field 'waitingId').GetValue($overlay),
        $five.Text, $week.Text)
}
$originalMode = $env:CODEX_QUOTA_TEST_MODE
try {
    $five = (Field 'five').GetValue($overlay)
    $week = (Field 'week').GetValue($overlay)
    $more = (Field 'more').GetValue($overlay)
    $refresh = (Field 'refresh').GetValue($overlay)
    $close = (Field 'close').GetValue($overlay)
    $tips = (Field 'tips').GetValue($overlay)
    Assert ($more.Right -le $refresh.Left -and $refresh.Right -le $close.Left) 'Buttons overlap.'
    Hover -1 19
    Assert $overlay.Visible 'Leaving should restore the overlay.'
    1..20 | ForEach-Object { Hover 300 19; Assert (!$overlay.Visible) 'Hover hiding flickers.' }
    Hover ($more.Left + 1) 19
    Assert $overlay.Visible 'Countdown button must stay usable.'
    (Field 'questionCardVisible').SetValue($overlay, $true)
    Hover ($more.Left + 1) 19
    Assert (!$overlay.Visible) 'Questions must hide the whole overlay.'
    (Field 'questionCardVisible').SetValue($overlay, $false)
    Hover -1 19
    Assert $overlay.Visible 'Answering should restore the overlay.'
    $refresh.PerformClick()
    Assert ((Field 'nextRead').GetValue($overlay) -eq [DateTime]::MinValue) 'Refresh action failed.'

    $boundsMethod = $overlay.GetType().GetMethod('OverlayBounds', $staticFlags)
    $dpiCases = @(
        @{ Dpi = 96; Height = 38; Clearance = 48 },
        @{ Dpi = 120; Height = 48; Clearance = 60 },
        @{ Dpi = 144; Height = 57; Clearance = 72 },
        @{ Dpi = 192; Height = 76; Clearance = 96 }
    )
    foreach ($case in $dpiCases) {
        $dpi = $case.Dpi
        foreach ($left in @(-1920, 0, 2560)) {
            foreach ($top in @(800, 950)) {
                foreach ($height in @(119, 240)) {
                    $composer = [Windows.Rect]::new($left, $top, 885, $height)
                    $bounds = $boundsMethod.Invoke($null, @($composer, $dpi))
                    Assert ($bounds.Left -eq $left -and $bounds.Width -eq 885) 'Horizontal alignment failed.'
                    Assert ($bounds.Height -eq $case.Height) 'DPI height failed.'
                    Assert ($top - $bounds.Bottom -eq $case.Clearance) 'DPI clearance failed.'
                }
            }
        }
    }
    $format = $overlay.GetType().GetMethod('FormatCountdown', $staticFlags)
    $now = [DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    Assert ($format.Invoke($null, @($now.AddHours(1).AddMinutes(2).AddSeconds(3), $now)) -eq '01小时 02分 03秒') 'Hourly countdown failed.'
    Assert ($format.Invoke($null, @($now.AddDays(2).AddSeconds(1), $now)) -eq '2天 00小时 00分 01秒') 'Weekly countdown failed.'
    Assert ($format.Invoke($null, @($now.AddSeconds(-1), $now)) -eq '等待额度更新') 'Expired countdown failed.'
    Assert ($format.Invoke($null, @($null, $now)) -eq '暂无数据') 'Unknown countdown failed.'
    $serializer = [Web.Script.Serialization.JavaScriptSerializer]::new()
    $missing = $serializer.DeserializeObject('{"rateLimits":{"primary":{"windowDurationMins":300,"usedPercent":null},"secondary":null}}')
    [void](Invoke-Private 'DisplayLimits' @($missing))
    Assert ($five.Text -eq '5h：暂无数据' -and $week.Text -eq '周额度：暂无数据') 'Missing data must not imply zero usage.'
    Assert ((Field 'fiveReset').GetValue($overlay) -eq $null -and (Field 'weekReset').GetValue($overlay) -eq $null) 'Missing resets must clear old deadlines.'

    $env:CODEX_QUOTA_TEST_MODE = 'normal'
    [void](Invoke-Private 'StartServer')
    $readerId = (Field 'server').GetValue($overlay).Id
    Pump-Until { $five.Text -eq '5h 剩余：93%' -and (Field 'server').GetValue($overlay) -eq $null }
    Assert ($week.Text -eq '周额度 剩余：75%') 'Duration mapping failed.'
    Assert (!(Get-Process -Id $readerId -ErrorAction SilentlyContinue)) 'Reader stayed alive after success.'
    $delay = ((Field 'nextRead').GetValue($overlay) - (Field 'requestedAt').GetValue($overlay)).TotalSeconds
    Assert ($delay -eq 30) 'Refresh cadence failed.'
    $before = $tips.GetToolTip($more)
    Start-Sleep -Milliseconds 1100
    [void](Invoke-Private 'UpdateCountdownTooltip')
    Assert ($before -ne $tips.GetToolTip($more)) 'Tooltip countdown did not advance.'

    $env:CODEX_QUOTA_TEST_MODE = 'error'
    [void](Invoke-Private 'StartServer')
    Pump-Until { $five.Text.Contains('未更新') }
    Assert ($week.Text.Contains('未更新')) 'Failed reads must mark both labels stale.'
    Assert ((Field 'server').GetValue($overlay) -eq $null) 'Failed reader leaked.'

    $env:CODEX_QUOTA_TEST_MODE = 'timeout'
    [void](Invoke-Private 'StartServer')
    Pump-Until { (Field 'ready').GetValue($overlay) }
    $readerId = (Field 'server').GetValue($overlay).Id
    (Field 'requestedAt').SetValue($overlay, [DateTime]::UtcNow.AddSeconds(-21))
    [void](Invoke-Private 'Tick')
    Assert ((Field 'server').GetValue($overlay) -eq $null) 'Timed-out reader leaked.'
    Assert (!(Get-Process -Id $readerId -ErrorAction SilentlyContinue)) 'Timed-out child did not exit.'
    Assert ($five.Text.Split('（').Count -eq 2) 'Stale marker was duplicated.'

    $env:CODEX_QUOTA_TEST_MODE = 'normal'
    [void](Invoke-Private 'StartServer')
    Pump-Until { $five.Text -eq '5h 剩余：93%' -and (Field 'server').GetValue($overlay) -eq $null }
    Assert (!$week.Text.Contains('未更新')) 'Successful recovery kept stale text.'
    $env:CODEX_QUOTA_TEST_MODE = 'timeout'
    [void](Invoke-Private 'StartServer')
    $readerId = (Field 'server').GetValue($overlay).Id
    $overlay.Dispose()
    Assert (!(Get-Process -Id $readerId -ErrorAction SilentlyContinue)) 'Disposal leaked a child process.'
    Write-Output 'PASS: hover/questions/buttons; 48 DPI/coordinate/input-height cases; countdowns; protocol; 30-second cadence; failure/recovery; child-process cleanup.'
} finally {
    $env:CODEX_QUOTA_TEST_MODE = $originalMode
    $overlay.Dispose()
    if (Test-Path -LiteralPath $fake) { Remove-Item -LiteralPath $fake }
}
