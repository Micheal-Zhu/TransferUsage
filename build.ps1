$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'

if (-not (Test-Path $msbuild)) {
    throw 'MSBuild for .NET Framework was not found.'
}

& (Join-Path $projectRoot 'tools\Generate-Icon.ps1') | Out-Null
& $msbuild (Join-Path $projectRoot 'BalanceDock.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m
if ($LASTEXITCODE -ne 0) { throw 'BalanceDock build failed.' }

& $msbuild (Join-Path $projectRoot 'tests\BalanceDock.SelfTests.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m
if ($LASTEXITCODE -ne 0) { throw 'Self-test build failed.' }

& (Join-Path $projectRoot 'tests\bin\Release\BalanceDock.SelfTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Self-tests failed.' }

$dist = Join-Path $projectRoot 'dist\BalanceDock'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$runtimeFiles = @(
    'BalanceDock.exe',
    'BalanceDock.exe.config',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll'
)
foreach ($runtimeFile in $runtimeFiles) {
    Copy-Item (Join-Path $projectRoot "bin\Release\$runtimeFile") -Destination $dist -Force
}
$debugSymbols = Join-Path $dist 'BalanceDock.pdb'
if (Test-Path $debugSymbols) { Remove-Item -LiteralPath $debugSymbols -Force }
Write-Output "Built: $dist\BalanceDock.exe"
