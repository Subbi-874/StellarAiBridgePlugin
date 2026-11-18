param(
    [string]$GameMiniDir = "E:\StarLauncher\game\release_3.7\game_mini",
    [string]$SecondaryGameDir = "G:\SteamLibrary\steamapps\common\星痕",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectPath = Join-Path $PSScriptRoot "plugin\Stellar.AgyBridge.csproj"
$SlotDir = Join-Path $GameMiniDir "stellar\plugins\agybridge"

Write-Host "[1/3] Building Stellar.AgyBridge ($Configuration)..."
dotnet build $ProjectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$OutDll = Join-Path $PSScriptRoot "plugin\bin\$Configuration\Stellar.AgyBridge.dll"
if (-not (Test-Path $OutDll)) {
    throw "Build output not found: $OutDll"
}

Write-Host "[2/3] Deploying to primary slot $SlotDir..."
New-Item -ItemType Directory -Force -Path $SlotDir | Out-Null
$DestDll = Join-Path $SlotDir "Stellar.AgyBridge.dll"

# Clean up any previously retired .old_* files that are no longer held open by any game process
Get-ChildItem -Path $SlotDir -Filter "*.old_*" -Force -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -Force -Path $_.FullName -ErrorAction SilentlyContinue
}

try {
    Copy-Item -Force -Path $OutDll -Destination $DestDll -ErrorAction Stop
    Set-Content -NoNewline -Encoding UTF8 -Path (Join-Path $SlotDir ".plugin-version") -Value "0.9.14"
    Write-Host "Deployed Stellar.AgyBridge.dll (v0.9.14) directly."
} catch {
    $Stamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $OldBak = Join-Path $SlotDir "Stellar.AgyBridge.old_$Stamp"
    Move-Item -Force -Path $DestDll -Destination $OldBak -ErrorAction Stop
    Copy-Item -Force -Path $OutDll -Destination $DestDll -ErrorAction Stop
    Set-Content -NoNewline -Encoding UTF8 -Path (Join-Path $SlotDir ".plugin-version") -Value "0.9.14"
    Write-Host "Staged Stellar.AgyBridge.dll (v0.9.14) via NTFS rename-aside (active upon next game launch)."
}

if ($SecondaryGameDir -and (Test-Path $SecondaryGameDir)) {
    $SecPluginsRoot = Join-Path $SecondaryGameDir "stellar\plugins"
    New-Item -ItemType Directory -Force -Path $SecPluginsRoot | Out-Null
    $SecSlot = Join-Path $SecPluginsRoot "agybridge"
    if (-not (Test-Path $SecSlot)) {
        Write-Host "[3/3] Creating NTFS Directory Junction: $SecSlot -> $SlotDir"
        New-Item -ItemType Junction -Path $SecSlot -Target $SlotDir | Out-Null
    } else {
        $Item = Get-Item $SecSlot -Force
        if ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            Write-Host "[3/3] Secondary junction verified: $SecSlot -> $($Item.Target)"
        } else {
            Write-Host "[3/3] Replacing non-junction folder with NTFS Junction: $SecSlot -> $SlotDir"
            Remove-Item -Recurse -Force -Path $SecSlot
            New-Item -ItemType Junction -Path $SecSlot -Target $SlotDir | Out-Null
        }
    }
}
