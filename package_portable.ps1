param(
    [string]$PythonVersion = "",
    [string]$Configuration = "Release",
    [string]$OutRoot = "",
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot

function Copy-FilteredTree {
    param(
        [string]$SourceDir,
        [string]$TargetDir
    )
    $ExcludedDirs = @(".venv", "runtime", "__pycache__", "tests", "linux_bpsr_workspace")
    $ExcludedMasks = @("*.log*", "*.db", "*.sqlite*", ".env*", "*.pyc", "LINUX_SERVER_PROMPT_GUIDE.md")

    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $SourceDir -Force) {
        if ($entry.PSIsContainer) {
            if ($ExcludedDirs -contains $entry.Name) {
                continue
            }
            Copy-FilteredTree -SourceDir $entry.FullName -TargetDir (Join-Path $TargetDir $entry.Name)
        } else {
            $skip = $false
            foreach ($mask in $ExcludedMasks) {
                if ($entry.Name -like $mask) {
                    $skip = $true
                    break
                }
            }
            if (-not $skip) {
                Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $TargetDir $entry.Name) -Force
            }
        }
    }
}

# 1. Read plugin version (single source of truth matching deploy.ps1)
$WsSource = Join-Path $RepoRoot "plugin\Network\BridgeWebSocketClient.cs"
$PluginVersion = "0.0.0"
$m = Select-String -Path $WsSource -Pattern '\["version"\]\s*=\s*"([0-9][0-9.]*)"' | Select-Object -First 1
if ($m) { $PluginVersion = $m.Matches[0].Groups[1].Value }

# 2. Resolve host Python and target embeddable version
$VenvPython = Join-Path $RepoRoot "mcp_bridge\.venv\Scripts\python.exe"
$HostPython = if (Test-Path -LiteralPath $VenvPython) {
    $VenvPython
} else {
    (Get-Command python -ErrorAction Stop).Source
}

$HostPyMinor = (& $HostPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')").Trim()
if ([string]::IsNullOrWhiteSpace($PythonVersion)) {
    $detected = (& $HostPython -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}')").Trim()
    $PythonVersion = if ($LASTEXITCODE -eq 0 -and $detected) { $detected } else { "3.13.3" }
}
$verParts = $PythonVersion.Split('.')
if ($verParts.Length -lt 2) { throw "Invalid PythonVersion '$PythonVersion'" }
$PyMajorMinor = "$($verParts[0]).$($verParts[1])"
$PyTag = "$($verParts[0])$($verParts[1])"

Write-Host "[1/6] Building Stellar.AgyBridge v$PluginVersion ($Configuration)..."
$ProjectPath = Join-Path $RepoRoot "plugin\Stellar.AgyBridge.csproj"
dotnet build $ProjectPath -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}
$OutDll = Join-Path $RepoRoot "plugin\bin\$Configuration\Stellar.AgyBridge.dll"
if (-not (Test-Path -LiteralPath $OutDll)) {
    throw "Build output not found: $OutDll"
}

# 3. Prepare clean bundle staging folder
$DistRoot = if ([string]::IsNullOrWhiteSpace($OutRoot)) { Join-Path $RepoRoot "dist" } else { $OutRoot }
$BundleName = "StellarAgyBridge-v$PluginVersion-portable"
$BundleDir = Join-Path $DistRoot $BundleName
if (Test-Path -LiteralPath $BundleDir) {
    Remove-Item -LiteralPath $BundleDir -Recurse -Force
}
$StellarDir = Join-Path $BundleDir "stellar"
$PluginOutDir = Join-Path $StellarDir "plugins\agybridge"
$BridgeRootDir = Join-Path $StellarDir "agybridge"
$RuntimeDir = Join-Path $BridgeRootDir "runtime"
$SitePackages = Join-Path $RuntimeDir "Lib\site-packages"
$StagedBridgeDir = Join-Path $BridgeRootDir "mcp_bridge"
New-Item -ItemType Directory -Force -Path $RuntimeDir, $SitePackages, $PluginOutDir | Out-Null

# 4. Download & unpack Python Embeddable package
$EmbedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
$TempZip = Join-Path ([IO.Path]::GetTempPath()) ("python-" + $PythonVersion + "-embed-amd64-" + [guid]::NewGuid().ToString("N") + ".zip")
Write-Host "[2/6] Downloading Python $PythonVersion Embeddable runtime from $EmbedUrl..."
try {
    Invoke-WebRequest -Uri $EmbedUrl -OutFile $TempZip -UseBasicParsing
    Expand-Archive -LiteralPath $TempZip -DestinationPath $RuntimeDir -Force
} finally {
    if (Test-Path -LiteralPath $TempZip) {
        Remove-Item -LiteralPath $TempZip -Force -ErrorAction SilentlyContinue
    }
}

$PthPath = Join-Path $RuntimeDir "python$PyTag._pth"
$PthLines = @(
    "python$PyTag.zip",
    ".",
    "Lib\site-packages",
    "..\mcp_bridge",
    "import site"
)
[System.IO.File]::WriteAllLines($PthPath, $PthLines, [System.Text.UTF8Encoding]::new($false))

# 5. Install Python dependencies into portable site-packages
Write-Host "[3/6] Installing mcp_bridge/requirements.txt into portable runtime..."
$ReqFile = Join-Path $RepoRoot "mcp_bridge\requirements.txt"
$prevPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $HostPython -m pip install --upgrade --target $SitePackages -r $ReqFile --only-binary=:all: --platform win_amd64 --python-version $PyMajorMinor 2>&1 | Out-Host
$pipExit = $LASTEXITCODE
if ($pipExit -ne 0) {
    if ($HostPyMinor -eq $PyMajorMinor) {
        Write-Host "Retrying pip install without --only-binary using matching host Python $HostPyMinor..."
        & $HostPython -m pip install --upgrade --target $SitePackages -r $ReqFile 2>&1 | Out-Host
        $pipExit = $LASTEXITCODE
    }
}
$ErrorActionPreference = $prevPref
if ($pipExit -ne 0) {
    throw "pip install into portable site-packages failed with exit code $pipExit"
}

$PyWinSys32 = Join-Path $SitePackages "pywin32_system32"
if (Test-Path -LiteralPath $PyWinSys32) {
    Copy-Item -Path (Join-Path $PyWinSys32 "*.dll") -Destination $RuntimeDir -Force
}

# 6. Stage stellar\agybridge (mcp_bridge, start_bridge.bat) and stellar\plugins\agybridge (DLL + .plugin-version)
Write-Host "[4/6] Staging stellar/agybridge and stellar/plugins/agybridge..."
Copy-FilteredTree -SourceDir (Join-Path $RepoRoot "mcp_bridge") -TargetDir $StagedBridgeDir
Copy-Item -LiteralPath (Join-Path $RepoRoot "start_bridge.bat") -Destination (Join-Path $BridgeRootDir "start_bridge.bat") -Force
Copy-Item -LiteralPath $OutDll -Destination (Join-Path $PluginOutDir "Stellar.AgyBridge.dll") -Force
[System.IO.File]::WriteAllText((Join-Path $PluginOutDir ".plugin-version"), $PluginVersion, [System.Text.UTF8Encoding]::new($false))

# 7. Verify portable runtime imports & start_bridge.bat CLI execution
Write-Host "[5/6] Verifying portable runtime..."
$PortablePy = Join-Path $RuntimeDir "python.exe"
& $PortablePy -c "import mcp, starlette, uvicorn, websockets, pydantic, i18n, chat_monitor; print('Portable runtime imports OK')"
if ($LASTEXITCODE -ne 0) {
    throw "Portable runtime import verification failed with exit code $LASTEXITCODE"
}
$PortableBat = Join-Path $BridgeRootDir "start_bridge.bat"
cmd /c "`"$PortableBat`" --help" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Portable start_bridge.bat --help failed with exit code $LASTEXITCODE"
}
Write-Host "Portable start_bridge.bat --help passed."

# Remove temporary bytecode caches and empty logs generated during self-verification
Get-ChildItem -LiteralPath $StagedBridgeDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.PSIsContainer -and $_.Name -eq "__pycache__") {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    } elseif (-not $_.PSIsContainer -and ($_.Name -like "bridge.log*" -or $_.Extension -eq ".pyc")) {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }
}

# 8. Create ZIP archive rooted at stellar\
if (-not $SkipZip) {
    $ZipPath = Join-Path $DistRoot "$BundleName.zip"
    Write-Host "[6/6] Creating ZIP archive: $ZipPath"
    Compress-Archive -Path $StellarDir -DestinationPath $ZipPath -Force
    $zipItem = Get-Item -LiteralPath $ZipPath
    Write-Host "Created portable package: $($zipItem.FullName) ($($zipItem.Length) bytes)"
} else {
    Write-Host "[6/6] Skipped ZIP creation (-SkipZip)."
}
