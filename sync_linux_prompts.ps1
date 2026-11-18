<#
.SYNOPSIS
  Pushes the updated LLM workspace files (v0.8.x actions) to the Linux BPSR server.

.DESCRIPTION
  Local mirror : StellarAgyBridge\mcp_bridge\prompts\linux_bpsr_workspace\
  Remote root  : /home/subbi/bpsr
  Files        : GEMINI.md, SKILL.md, scripts/bridge_cli.py  (bpsr_guard.py / hooks.json are unchanged)

  Safety:
   1. Downloads the CURRENT remote files and compares them with the pre-update local backup.
      If the server copy differs (it was edited elsewhere), the script stops unless -Force is given.
   2. Keeps a timestamped .bak of every remote file it overwrites.
   3. -WhatIf only prints what would happen (no connection is made).

.EXAMPLE
  .\sync_linux_prompts.ps1 -WhatIf
  .\sync_linux_prompts.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$HostName   = "192.168.2.20",
    [string]$User       = "subbi",
    [string]$KeyPath    = (Join-Path $env:USERPROFILE ".gemini\id_rsa"),
    [string]$RemoteRoot = "/home/subbi/bpsr",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$mirror   = Join-Path $PSScriptRoot "mcp_bridge\prompts\linux_bpsr_workspace"
$original = Join-Path $PSScriptRoot "_backup_pre_v080\mcp_bridge\prompts\linux_bpsr_workspace"
$skillDir = "$RemoteRoot/.agents/skills/stellar-bridge"

# local file -> remote path
$map = @(
    @{ Local = "GEMINI.md";      Remote = "$RemoteRoot/GEMINI.md" },
    @{ Local = "SKILL.md";       Remote = "$skillDir/SKILL.md" },
    @{ Local = "bridge_cli.py";  Remote = "$skillDir/scripts/bridge_cli.py" },
    @{ Local = "bpsr_guard.py";  Remote = "$RemoteRoot/.agents/bpsr_guard.py" }
)

foreach ($m in $map) {
    if (-not (Test-Path (Join-Path $mirror $m.Local))) { throw "Missing local file: $($m.Local)" }
}

if ($WhatIfPreference) {
    $map | ForEach-Object { Write-Host "[WhatIf] $($_.Local) -> ${User}@${HostName}:$($_.Remote)" }
    return
}

foreach ($tool in "ssh", "scp") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' not found (enable the Windows OpenSSH client)." }
}
if (-not (Test-Path $KeyPath)) { throw "SSH key not found: $KeyPath" }

$sshOpts = @("-i", $KeyPath, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new")
$stamp   = Get-Date -Format "yyyyMMdd_HHmmss"
$tmp     = Join-Path $env:TEMP "bpsr_sync_$stamp"
New-Item -ItemType Directory -Force $tmp | Out-Null

try {
    # 1. Verify the server copy still equals the pre-update mirror
    $diverged = @()
    foreach ($m in $map) {
        $dl = Join-Path $tmp $m.Local
        & scp @sshOpts "${User}@${HostName}:$($m.Remote)" $dl
        if ($LASTEXITCODE -ne 0) { throw "Could not download $($m.Remote)" }
        $base = Join-Path $original $m.Local
        if ((Test-Path $base) -and ((Get-FileHash $dl).Hash -ne (Get-FileHash $base).Hash)) { $diverged += $m.Local }
    }
    if ($diverged.Count -gt 0 -and -not $Force) {
        throw "Server copy differs from the local pre-update backup for: $($diverged -join ', '). Review it, then re-run with -Force to overwrite."
    }

    # 2. Backup + upload
    foreach ($m in $map) {
        & ssh @sshOpts "${User}@${HostName}" "cp -p '$($m.Remote)' '$($m.Remote).bak_$stamp'"
        if ($LASTEXITCODE -ne 0) { throw "Backup failed: $($m.Remote)" }
        & scp @sshOpts (Join-Path $mirror $m.Local) "${User}@${HostName}:$($m.Remote)"
        if ($LASTEXITCODE -ne 0) { throw "Upload failed: $($m.Remote)" }
        Write-Host "Updated $($m.Remote)  (backup: .bak_$stamp)"
    }

    # 3. Syntax-check the scripts on the server
    & ssh @sshOpts "${User}@${HostName}" "python3 -m py_compile '$skillDir/scripts/bridge_cli.py' '$RemoteRoot/.agents/bpsr_guard.py' && echo 'bridge_cli.py & bpsr_guard.py syntax OK'"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
