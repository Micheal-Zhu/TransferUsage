$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$releaseRoot = Join-Path $projectRoot 'dist\BalanceDock'
$publishRoot = Join-Path $projectRoot '发布'
$stageRoot = Join-Path $projectRoot 'obj\single-file-stage'
$payloadPath = Join-Path $projectRoot 'obj\single-file-payload.zip'
$launcherPath = Join-Path $publishRoot 'BalanceDock-Portable.exe'

if (-not (Test-Path $msbuild)) { throw 'MSBuild for .NET Framework was not found.' }
if (-not (Test-Path $csc)) { throw 'C# compiler was not found.' }

& (Join-Path $projectRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$runtimeFiles = @(
    'BalanceDock.exe',
    'BalanceDock.exe.config',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll'
)
foreach ($runtimeFile in $runtimeFiles) {
    $source = Join-Path $releaseRoot $runtimeFile
    if (-not (Test-Path $source)) { throw "Required runtime file is missing: $runtimeFile" }
}

if (Test-Path $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path $payloadPath) { Remove-Item -LiteralPath $payloadPath -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
foreach ($runtimeFile in $runtimeFiles) {
    Copy-Item (Join-Path $releaseRoot $runtimeFile) -Destination $stageRoot -Force
}

# The staging list is intentionally explicit: user station data and WebView2 profiles are never copied.
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $payloadPath -CompressionLevel Optimal -Force

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
if (Test-Path $launcherPath) { Remove-Item -LiteralPath $launcherPath -Force }
$iconPath = Join-Path $projectRoot 'Assets\BalanceDock.ico'
$launcherSource = Join-Path $projectRoot 'SingleFileLauncher.cs'
$launcherArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    "/out:$launcherPath",
    "/win32icon:$iconPath",
    "/resource:$payloadPath,payload.zip",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    '/reference:System.Windows.Forms.dll',
    $launcherSource
)
& $csc @launcherArgs
if ($LASTEXITCODE -ne 0) { throw 'Single-file launcher build failed.' }

Copy-Item (Join-Path $projectRoot '使用教程.md') -Destination (Join-Path $publishRoot '使用教程.md') -Force

$payloadHash = (Get-FileHash -Algorithm SHA256 $launcherPath).Hash
Write-Output "Built: $launcherPath"
Write-Output "SHA256: $payloadHash"
