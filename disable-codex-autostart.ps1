$ErrorActionPreference = 'Stop'
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$service.GetFolder('\').DeleteTask('CodexQuota-OnCodexStart', 0)
Write-Output 'Disabled: CodexQuota-OnCodexStart'
