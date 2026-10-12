$ErrorActionPreference = 'Stop'
$taskName = 'CodexQuota-OnCodexStart'
$executable = Join-Path $PSScriptRoot 'CodexQuota.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'CodexQuota.exe was not found.' }
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$definition = $service.NewTask(0)
$definition.RegistrationInfo.Description = 'Start the quota overlay when the Codex desktop app creates its process. No boot or logon trigger.'
$definition.Principal.UserId = $userSid
$definition.Principal.LogonType = 3 # Existing interactive user session; no password stored.
$definition.Principal.RunLevel = 0
$definition.Settings.Enabled = $true
$definition.Settings.Hidden = $true
$definition.Settings.MultipleInstances = 2
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.ExecutionTimeLimit = 'PT0S'
$definition.Settings.StartWhenAvailable = $false
$trigger = $definition.Triggers.Create(0) # Event trigger only.
$trigger.Enabled = $true
$trigger.Delay = 'PT3S'
$trigger.Subscription = @'
<QueryList><Query Id="0" Path="Microsoft-Windows-AppModel-Runtime/Admin"><Select Path="Microsoft-Windows-AppModel-Runtime/Admin">*[System[EventID=201] and EventData[Data[@Name='ApplicationName']='OpenAI.Codex_2p2nqsd0c76g0!App']]</Select></Query></QueryList>
'@
$action = $definition.Actions.Create(0)
$action.Path = $executable
$action.WorkingDirectory = $PSScriptRoot
$registered = $service.GetFolder('\').RegisterTaskDefinition($taskName, $definition, 6, $userSid, $null, 3)
Write-Output "Enabled: $($registered.Name)"
