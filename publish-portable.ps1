[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$projectPath = Join-Path $PSScriptRoot 'PoiTime.WinUI\PoiTime.WinUI.csproj'
$versionProperties = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)
$version = [string]$versionProperties.Project.PropertyGroup.VersionPrefix
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'VersionPrefix must use MAJOR.MINOR.PATCH without leading zeroes.'
}
$packageManifest = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PoiTime.WinUI\Package.appxmanifest') -Raw)
$appManifest = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PoiTime.WinUI\app.manifest') -Raw)
if ($packageManifest.Package.Identity.Version -ne "$version.0" -or
    $appManifest.assembly.assemblyIdentity.version -ne "$version.0") {
    throw 'Manifest versions must match VersionPrefix followed by .0.'
}
$publishDir = Join-Path $OutputRoot "PoiTime-Portable-$version-win-x64"
$zipPath = "$publishDir.zip"

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with MSBuild is required to publish.' }
$installations = @(& $vswhere -products '*' -requires Microsoft.Component.MSBuild -property installationPath)
$msbuild = $null
$crtDirectory = $null
foreach ($installation in $installations) {
    $candidate = Join-Path $installation 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (!$msbuild -and (Test-Path -LiteralPath $candidate)) { $msbuild = $candidate }
    $redistRoot = Join-Path $installation 'VC\Redist\MSVC'
    if (Test-Path -LiteralPath $redistRoot) {
        $versions = Get-ChildItem -LiteralPath $redistRoot -Directory |
            Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending
        foreach ($redistVersion in $versions) {
            $x64Directory = Join-Path $redistVersion.FullName 'x64'
            if (Test-Path -LiteralPath $x64Directory) {
                $candidateCrt = Get-ChildItem -LiteralPath $x64Directory -Directory -Filter 'Microsoft.VC*.CRT' |
                    Select-Object -First 1
                if ($candidateCrt -and (!$crtDirectory -or [version]$redistVersion.Name -gt $crtVersion)) {
                    $crtDirectory = $candidateCrt.FullName
                    $crtVersion = [version]$redistVersion.Name
                }
            }
        }
    }
}
if (!$msbuild) { throw 'MSBuild.exe was not found.' }
if (!$crtDirectory) { throw 'Install the Visual C++ build tools to provide redistributable x64 CRT DLLs.' }
if ((Test-Path -LiteralPath $publishDir) -and @(Get-ChildItem -LiteralPath $publishDir -Force).Count -gt 0) {
    throw "Use a fresh output directory: $publishDir"
}
if (Test-Path -LiteralPath $zipPath) { throw "The release archive already exists: $zipPath" }
[IO.Directory]::CreateDirectory($publishDir) | Out-Null

$buildArguments = @(
    $projectPath, '/nologo', '/restore', '/t:Publish', '/m:1', '/nodeReuse:false', '/v:minimal',
    '/p:Configuration=Release', '/p:Platform=x64', '/p:RuntimeIdentifier=win-x64',
    '/p:RuntimeIdentifiers=',
    '/p:DisableTransitiveFrameworkReferenceDownloads=true',
    '/p:PublishProfile=win-x64', '/p:SelfContained=true',
    '/p:WindowsAppSDKSelfContained=true', '/p:PublishSingleFile=false',
    '/p:PublishTrimmed=false', '/p:PublishReadyToRun=false',
    "/p:PublishDir=$publishDir\"
)
if ($Offline) {
    $nugetCache = Join-Path $env:USERPROFILE '.nuget\packages'
    $buildArguments += "/p:RestoreSources=$nugetCache"
    $buildArguments += '/p:NuGetAudit=false'
}

Push-Location $PSScriptRoot
$previousSdksPath = $env:MSBuildSDKsPath
$previousWorkloadResolver = $env:MSBuildEnableWorkloadResolver
try {
    # Native-only Build Tools installations may lack the .NET SDK resolver.
    # Use the SDK selected by global.json without modifying the VS installation.
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK discovery failed.' }
    $sdkInstallation = & dotnet --list-sdks | Where-Object { $_ -match "^$([regex]::Escape($sdkVersion)) \[" } |
        Select-Object -First 1
    if (!$sdkInstallation -or $sdkInstallation -notmatch '\[(.+)\]') { throw '.NET SDK path was not found.' }
    $env:MSBuildSDKsPath = Join-Path $Matches[1] "$sdkVersion\Sdks"
    $env:MSBuildEnableWorkloadResolver = 'false'
    & $msbuild @buildArguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
    $deploymentProperties = & $msbuild $projectPath /nologo /p:Platform=x64 /p:Configuration=Release `
        /p:RuntimeIdentifier=win-x64 /p:PublishProfile=win-x64 `
        -getProperty:WindowsAppSDKSelfContained,WindowsAppSdkBootstrapInitialize,DefineConstants |
        Out-String | ConvertFrom-Json
    if ($deploymentProperties.Properties.WindowsAppSDKSelfContained -ne 'true' -or
        $deploymentProperties.Properties.WindowsAppSdkBootstrapInitialize -eq 'true' -or
        $deploymentProperties.Properties.DefineConstants -notmatch 'MICROSOFT_WINDOWSAPPSDK_SELFCONTAINED') {
        throw 'The release still uses Windows App Runtime package bootstrap initialization.'
    }
}
finally {
    $env:MSBuildSDKsPath = $previousSdksPath
    $env:MSBuildEnableWorkloadResolver = $previousWorkloadResolver
    Pop-Location
}

# Redistributable CRT binaries are deployed beside the EXE; no installer is run.
Get-ChildItem -LiteralPath $crtDirectory -File -Filter '*.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $publishDir
}
$requiredFiles = @(
    'PoiTime.WinUI.exe', 'PoiTime.WinUI.dll', 'PoiTime.WinUI.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
    'Microsoft.UI.Xaml.dll', 'Microsoft.WindowsAppRuntime.dll',
    'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll',
    'PoiTime.WinUI.pri',
    'Assets\favicon.ico', 'voices\144\config.json'
)
foreach ($file in $requiredFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDir $file))) { throw "Missing release file: $file" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDir 'PoiTime.WinUI.runtimeconfig.json') -Raw |
    ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
    throw 'The release still requires a shared .NET runtime.'
}
$exeVersion = (Get-Item -LiteralPath (Join-Path $publishDir 'PoiTime.WinUI.exe')).VersionInfo.FileVersion
if ($exeVersion -ne "$version.0") { throw "Unexpected EXE file version: $exeVersion" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PORTABLE-README.txt') -Destination $publishDir
Compress-Archive -LiteralPath $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII -Value "$($hash.Hash)  $([IO.Path]::GetFileName($zipPath))"
Write-Output "Portable release: $publishDir"
Write-Output "Archive: $zipPath"
