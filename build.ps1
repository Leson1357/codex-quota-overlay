$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Web.Extensions.dll', 'WPF\WindowsBase.dll',
    'WPF\UIAutomationClient.dll', 'WPF\UIAutomationTypes.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$PSScriptRoot\app.manifest" "/out:$PSScriptRoot\CodexQuota.exe" @references "$PSScriptRoot\CodexQuota.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
