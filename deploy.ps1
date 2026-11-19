param(
    # Root that holds the launcher's versioned game folders (release_3.7, release_4.7, ...)
    [string]$GameRoot = "E:\StarLauncher\game",
    # Optional explicit override; when empty the newest release_*\game_mini under $GameRoot is used
    [string]$GameMiniDir = "",
    [string]$SecondaryGameDir = "G:\SteamLibrary\steamapps\common\星痕",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$ProjectPath = Join-Path $PSScriptRoot "plugin\Stellar.AgyBridge.csproj"

# --- Resolve the active game install (the launcher replaces release_X.Y on patches) ---
if (-not $GameMiniDir) {
    $candidates = Get-ChildItem -Path $GameRoot -Directory -Filter "release_*" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "game_mini") } |
        Sort-Object {
            $v = $_.Name -replace '^release_', ''
            try { [version]$v } catch { [version]"0.0" }
        } -Descending
    if (-not $candidates) { throw "No release_*\game_mini folder found under $GameRoot" }
    $GameMiniDir = Join-Path $candidates[0].FullName "game_mini"
}
if (-not (Test-Path $GameMiniDir)) { throw "Game directory not found: $GameMiniDir" }
$SlotDir = Join-Path $GameMiniDir "stellar\plugins\agybridge"
Write-Host "Target game: $GameMiniDir"

# --- Single source of truth for the plugin version: the hello handshake in BridgeWebSocketClient.cs ---
$wsSource = Join-Path $PSScriptRoot "plugin\Network\BridgeWebSocketClient.cs"
$PluginVersion = "0.0.0"
$m = Select-String -Path $wsSource -Pattern '\["version"\]\s*=\s*"([0-9][0-9.]*)"' | Select-Object -First 1
if ($m) { $PluginVersion = $m.Matches[0].Groups[1].Value }

Write-Host "[1/3] Building Stellar.AgyBridge v$PluginVersion ($Configuration)..."
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
$VersionFile = Join-Path $SlotDir ".plugin-version"

# Clean up any previously retired .old_* files that are no longer held open by any game process
Get-ChildItem -Path $SlotDir -Filter "*.old_*" -Force -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item -Force -Path $_.FullName -ErrorAction SilentlyContinue
}

try {
    Copy-Item -Force -Path $OutDll -Destination $DestDll -ErrorAction Stop
    Set-Content -NoNewline -Encoding UTF8 -Path $VersionFile -Value $PluginVersion
    Write-Host "Deployed Stellar.AgyBridge.dll (v$PluginVersion) directly."
} catch {
    $Stamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $OldBak = Join-Path $SlotDir "Stellar.AgyBridge.old_$Stamp"
    Move-Item -Force -Path $DestDll -Destination $OldBak -ErrorAction Stop
    Copy-Item -Force -Path $OutDll -Destination $DestDll -ErrorAction Stop
    Set-Content -NoNewline -Encoding UTF8 -Path $VersionFile -Value $PluginVersion
    Write-Host "Staged Stellar.AgyBridge.dll (v$PluginVersion) via NTFS rename-aside (active upon next game launch)."
}

if ($SecondaryGameDir -and (Test-Path $SecondaryGameDir)) {
    $SecPluginsRoot = Join-Path $SecondaryGameDir "stellar\plugins"
    New-Item -ItemType Directory -Force -Path $SecPluginsRoot | Out-Null
    $SecSlot = Join-Path $SecPluginsRoot "agybridge"

    # -Force is required: Test-Path/Get-Item hide a junction whose target no longer exists (e.g. after the launcher
    # replaced release_3.7 with release_4.7), which previously left a dangling link behind.
    $Item = Get-Item $SecSlot -Force -ErrorAction SilentlyContinue
    if (-not $Item) {
        Write-Host "[3/3] Creating NTFS Directory Junction: $SecSlot -> $SlotDir"
        New-Item -ItemType Junction -Path $SecSlot -Target $SlotDir | Out-Null
    } elseif ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        $current = @($Item.Target)[0]
        if ($current -and ([IO.Path]::GetFullPath($current).TrimEnd('\') -ieq [IO.Path]::GetFullPath($SlotDir).TrimEnd('\')) -and (Test-Path $current)) {
            Write-Host "[3/3] Secondary junction verified: $SecSlot -> $current"
        } else {
            Write-Host "[3/3] Repairing stale junction ($current): $SecSlot -> $SlotDir"
            $Item.Delete()   # removes only the link, never the target's contents
            New-Item -ItemType Junction -Path $SecSlot -Target $SlotDir | Out-Null
        }
    } else {
        Write-Host "[3/3] Replacing non-junction folder with NTFS Junction: $SecSlot -> $SlotDir"
        Remove-Item -Recurse -Force -Path $SecSlot
        New-Item -ItemType Junction -Path $SecSlot -Target $SlotDir | Out-Null
    }
}
