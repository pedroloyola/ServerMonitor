<#
.SYNOPSIS
    QA-ONLY. Generates SSH config fixture profiles for the Debug --qa-ssh-config harness (M14.4a).

.DESCRIPTION
    Creates one fake user profile per scenario under -Root, each with its own .ssh\config, so
    "Import from SSH config" can be exercised by hand without touching the real ~/.ssh:

      empty    .ssh exists, no config                     -> "No ~/.ssh/config found"
      error    Host line with an unterminated quote       -> classified error, nothing imported
      big      Include conf.d/* with 640 hosts             -> capped at 500 + mix of importable /
                                                              blocked / ambiguous / warnings
      blocked  ProxyJump, ProxyCommand, Match with proxy,  -> listed, not importable (one contrast
               unverifiable Include                          host with ProxyJump none is importable)
      normal   5 hosts with HostName/User/Port/IdentityFile -> all importable; IdentityFile points
               (2 of them via Include)                       at key paths that do NOT exist

    Nothing outside -Root is written. No key file is ever created.

.PARAMETER Root
    Output directory. Defaults to a new directory under %TEMP%. Must be missing or empty, and may
    not be the real user profile.

.EXAMPLE
    pwsh -NoProfile -File tools/qa/ssh-config-fixtures.ps1
    dotnet run -c Debug --project src/ServerMonitor.App/ServerMonitor.App.csproj -- --qa-ssh-config "<Root>\normal"
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path ([System.IO.Path]::GetTempPath()) ('serveralyzer-ssh-qa-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = [System.IO.Path]::GetFullPath($Root)
$realProfile = [Environment]::GetFolderPath('UserProfile')
if ($Root.TrimEnd('\') -ieq $realProfile.TrimEnd('\')) {
    throw "Refusing to write fixtures into the real user profile ($realProfile)."
}

if (Test-Path -LiteralPath $Root) {
    if (Get-ChildItem -LiteralPath $Root -Force | Select-Object -First 1) {
        throw "Root '$Root' is not empty; choose a new directory."
    }
}
else {
    New-Item -ItemType Directory -Path $Root | Out-Null
}

$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-Fixture([string] $Path, [string] $Text) {
    $directory = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), $utf8)
}

function Ssh([string] $Scenario, [string] $Relative) {
    Join-Path (Join-Path (Join-Path $Root $Scenario) '.ssh') $Relative
}

# (a) empty: ~/.ssh exists but there is no config.
New-Item -ItemType Directory -Path (Join-Path (Join-Path $Root 'empty') '.ssh') -Force | Out-Null

# (b) error: a Host line with an unterminated quote makes ssh reject the whole file.
Write-Fixture (Ssh 'error' 'config') @'
Host "broken
    HostName 10.0.0.1
    User nobody
'@

# (c) big: 640 hosts across 4 included files, more than the 500 the import lists.
Write-Fixture (Ssh 'big' 'config') @'
# QA fixture: many hosts via Include.
Include conf.d/*.conf

Host *
    ServerAliveInterval 30
'@
$hostNumber = 0
foreach ($file in 1..4) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($i in 1..160) {
        $hostNumber++
        $name = 'qa-host-{0:D3}' -f $hostNumber
        [void]$builder.AppendLine("Host $name")
        [void]$builder.AppendLine("    HostName 10.20.$([math]::Floor($hostNumber / 250)).$($hostNumber % 250 + 1)")
        [void]$builder.AppendLine("    User qa$($hostNumber % 7)")
        [void]$builder.AppendLine("    Port $(2200 + ($hostNumber % 50))")
        if ($hostNumber % 4 -eq 0) {
            [void]$builder.AppendLine('    ProxyJump qa-bastion')                        # blocked
        }
        elseif ($hostNumber % 5 -eq 0) {
            [void]$builder.AppendLine("    IdentityFile ~/.ssh/qa_missing_a_$hostNumber") # ambiguous
            [void]$builder.AppendLine("    IdentityFile ~/.ssh/qa_missing_b_$hostNumber")
        }
        elseif ($hostNumber % 7 -eq 0) {
            [void]$builder.AppendLine('    HostKeyAlias qa-alias')                       # warning
            [void]$builder.AppendLine('    ForwardAgent no')                             # ignored
        }
        else {
            [void]$builder.AppendLine("    IdentityFile ~/.ssh/qa_missing_$hostNumber")  # importable
        }
    }

    Write-Fixture (Ssh 'big' ("conf.d\{0:D2}-hosts.conf" -f $file)) $builder.ToString()
}

# (d) blocked: every host is listed but only the contrast host is importable.
Write-Fixture (Ssh 'blocked' 'config') @'
# QA fixture: every reason a host is not imported.
Include conf.d/*.missing

Host ok-contrast
    ProxyJump none
    HostName 10.30.0.1
    User qa

Host via-proxyjump
    HostName 10.30.0.2
    ProxyJump qa-bastion

Host via-proxycommand
    HostName 10.30.0.3
    ProxyCommand ssh -W %h:%p qa-bastion

Host via-include
    HostName 10.30.0.4
    Include ../outside.conf

Host via-match
    HostName 10.30.0.5
    User qa

Match host via-match
    ProxyJump qa-bastion
'@
Write-Fixture (Join-Path (Join-Path $Root 'blocked') 'outside.conf') @'
# Outside ~/.ssh: must never be opened by the import.
ProxyJump none
'@

# (e) normal: 5 importable hosts; 2 come from an Include. Keys are paths only and do not exist.
Write-Fixture (Ssh 'normal' 'config') @'
# QA fixture: plain importable hosts. The Include is at the top on purpose: placed after a Host
# line it would belong to that block, and ssh would never apply the hosts it brings in.
Include conf.d/*.conf

Host web
    HostName 10.40.0.10
    User deploy
    Port 22
    IdentityFile ~/.ssh/qa_missing_web

Host db
    HostName db.qa.internal
    User postgres
    Port 2222
    IdentityFile ~/.ssh/qa_missing_db

Host nas
    HostName 10.40.0.30
    User admin
    Port 22022
    IdentityFile ~/.ssh/qa_missing_nas
'@
Write-Fixture (Ssh 'normal' 'conf.d\10-lab.conf') @'
Host lab-a
    HostName 10.40.1.1
    User lab
    Port 2201
    IdentityFile ~/.ssh/qa_missing_lab_a

Host lab-b
    HostName 10.40.1.2
    User lab
    Port 2202
    IdentityFile ~/.ssh/qa_missing_lab_b
'@

$project = 'src/ServerMonitor.App/ServerMonitor.App.csproj'
Write-Output "SSH config QA fixtures written to: $Root"
foreach ($scenario in 'empty', 'error', 'big', 'blocked', 'normal') {
    $directory = Join-Path $Root $scenario
    Write-Output ("  {0,-8} dotnet run -c Debug --project {1} -- --qa-ssh-config `"{2}`"" -f $scenario, $project, $directory)
}
