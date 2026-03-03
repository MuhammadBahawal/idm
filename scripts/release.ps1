param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$ChromiumExtensionIds = "gnpallpkcdihlckdkddppkhgblokapdj",
    [string]$FirefoxExtensionIds = "mydm@mydm.app",
    [switch]$SkipTests,
    [switch]$SkipExtensionBuild,
    [switch]$SkipInstaller,
    [switch]$SkipNpmInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
}

function Parse-Ids {
    param(
        [string]$Raw,
        [string]$Pattern
    )

    return $Raw.Split(@(",", ";", " ", "`n", "`r", "`t"), [System.StringSplitOptions]::RemoveEmptyEntries) `
        | ForEach-Object { $_.Trim() } `
        | Where-Object { $_ -match $Pattern } `
        | Sort-Object -Unique
}

function Write-NativeHostManifests {
    param(
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$HostExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$ChromiumIds,
        [Parameter(Mandatory = $true)][string[]]$FirefoxIds
    )

    $chromiumManifest = [ordered]@{
        name = "com.mydm.native"
        description = "MyDM Native Messaging Host"
        path = $HostExecutablePath
        type = "stdio"
        allowed_origins = @($ChromiumIds | ForEach-Object { "chrome-extension://$_/" })
    }

    $firefoxManifest = [ordered]@{
        name = "com.mydm.native"
        description = "MyDM Native Messaging Host"
        path = $HostExecutablePath
        type = "stdio"
        allowed_extensions = @($FirefoxIds)
    }

    $chromiumPath = Join-Path $OutputDirectory "com.mydm.native.chromium.json"
    $firefoxPath = Join-Path $OutputDirectory "com.mydm.native.firefox.json"
    $legacyPath = Join-Path $OutputDirectory "com.mydm.native.json"

    $chromiumManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $chromiumPath -Encoding UTF8
    $firefoxManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $firefoxPath -Encoding UTF8
    $chromiumManifest | ConvertTo-Json -Depth 5 | Set-Content -Path $legacyPath -Encoding UTF8
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$releaseRoot = Join-Path $repoRoot "artifacts\release\$Version"
$publishRoot = Join-Path $releaseRoot "publish"
$appPublish = Join-Path $publishRoot "app"
$hostPublish = Join-Path $publishRoot "nativehost"
$bundleRoot = Join-Path $releaseRoot "bundle\MyDM"
$extensionReleaseRoot = Join-Path $releaseRoot "extensions"

$chromiumIds = @(Parse-Ids -Raw $ChromiumExtensionIds -Pattern "^[a-pA-P]{32}$")
$firefoxIds = @(Parse-Ids -Raw $FirefoxExtensionIds -Pattern "^[A-Za-z0-9._@+\-]{3,}$")
if ($chromiumIds.Count -eq 0) {
    throw "No valid Chromium extension IDs were provided."
}
if ($firefoxIds.Count -eq 0) {
    throw "No valid Firefox extension IDs were provided."
}

Invoke-Step -Name "Clean release directory" -Action {
    if (Test-Path $releaseRoot) {
        Remove-Item -Path $releaseRoot -Recurse -Force
    }
    New-Item -Path $releaseRoot -ItemType Directory | Out-Null
    New-Item -Path $publishRoot -ItemType Directory | Out-Null
    New-Item -Path $bundleRoot -ItemType Directory | Out-Null
    New-Item -Path $extensionReleaseRoot -ItemType Directory | Out-Null
}

Invoke-Step -Name "Restore + build solution" -Action {
    dotnet restore (Join-Path $repoRoot "MyDM.slnx")
    dotnet build (Join-Path $repoRoot "MyDM.slnx") -c $Configuration
}

if (-not $SkipTests) {
    Invoke-Step -Name "Run tests" -Action {
        dotnet test (Join-Path $repoRoot "tests\MyDM.Core.Tests\MyDM.Core.Tests.csproj") -c $Configuration
    }
}

Invoke-Step -Name "Publish desktop app" -Action {
    dotnet publish (Join-Path $repoRoot "src\MyDM.App\MyDM.App.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $appPublish
}

Invoke-Step -Name "Publish native host" -Action {
    dotnet publish (Join-Path $repoRoot "src\MyDM.NativeHost\MyDM.NativeHost.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $hostPublish
}

Invoke-Step -Name "Assemble app bundle" -Action {
    Copy-Item -Path (Join-Path $appPublish "*") -Destination $bundleRoot -Recurse -Force
    $hostExe = Join-Path $hostPublish "MyDM.NativeHost.exe"
    if (-not (Test-Path $hostExe)) {
        throw "Published native host not found at: $hostExe"
    }
    Copy-Item -Path $hostExe -Destination (Join-Path $bundleRoot "MyDM.NativeHost.exe") -Force
    Write-NativeHostManifests -OutputDirectory $bundleRoot -HostExecutablePath (Join-Path $bundleRoot "MyDM.NativeHost.exe") -ChromiumIds $chromiumIds -FirefoxIds $firefoxIds
}

if (-not $SkipExtensionBuild) {
    Invoke-Step -Name "Build extension packages (Chromium + Firefox)" -Action {
        Push-Location (Join-Path $repoRoot "extension")
        try {
            if (-not $SkipNpmInstall) {
                npm ci
            }
            npm run build:all
        }
        finally {
            Pop-Location
        }

        $chromiumPack = Join-Path $repoRoot "extension\dist-pack\chromium"
        $firefoxPack = Join-Path $repoRoot "extension\dist-pack\firefox"
        $bundleExtensions = Join-Path $bundleRoot "extensions"
        New-Item -Path $bundleExtensions -ItemType Directory -Force | Out-Null

        Copy-Item -Path $chromiumPack -Destination (Join-Path $bundleExtensions "chromium") -Recurse -Force
        Copy-Item -Path $firefoxPack -Destination (Join-Path $bundleExtensions "firefox") -Recurse -Force

        $chromiumZip = Join-Path $extensionReleaseRoot "mydm-extension-chromium.zip"
        $firefoxZip = Join-Path $extensionReleaseRoot "mydm-extension-firefox.zip"
        if (Test-Path $chromiumZip) { Remove-Item $chromiumZip -Force }
        if (Test-Path $firefoxZip) { Remove-Item $firefoxZip -Force }
        Compress-Archive -Path (Join-Path $chromiumPack "*") -DestinationPath $chromiumZip -Force
        Compress-Archive -Path (Join-Path $firefoxPack "*") -DestinationPath $firefoxZip -Force
    }
}

$releaseSummaryPath = Join-Path $releaseRoot "release-summary.txt"
Invoke-Step -Name "Write release summary" -Action {
    @(
        "MyDM Release Summary"
        "===================="
        "Version: $Version"
        "Configuration: $Configuration"
        "Runtime: $Runtime"
        "Bundle: $bundleRoot"
        "Chromium IDs: $($chromiumIds -join ', ')"
        "Firefox IDs: $($firefoxIds -join ', ')"
        ""
        "App EXE: $(Join-Path $bundleRoot 'MyDM.App.exe')"
        "Native Host EXE: $(Join-Path $bundleRoot 'MyDM.NativeHost.exe')"
        "Chromium Manifest: $(Join-Path $bundleRoot 'com.mydm.native.chromium.json')"
        "Firefox Manifest: $(Join-Path $bundleRoot 'com.mydm.native.firefox.json')"
    ) | Set-Content -Path $releaseSummaryPath -Encoding UTF8
}

if (-not $SkipInstaller) {
    Invoke-Step -Name "Build installer (Inno Setup)" -Action {
        $iscc = @(
            (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

        if (-not $iscc) {
            Write-Warning "Inno Setup compiler (ISCC.exe) not found. Installer step skipped."
            return
        }

        $issPath = Join-Path $repoRoot "installer\MyDM.iss"
        & $iscc "/DMyDMVersion=$Version" "/DMyDMSourceDir=$bundleRoot" "/DReleaseRoot=$releaseRoot" $issPath
    }
}

Write-Host ""
Write-Host "Release completed." -ForegroundColor Green
Write-Host "Artifacts: $releaseRoot"
