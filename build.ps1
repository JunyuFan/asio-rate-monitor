$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.x C# compiler was not found.' }

$releasePath = Join-Path $projectRoot 'dist'
$testPath = Join-Path $projectRoot 'artifacts\tests'
New-Item -ItemType Directory -Path $releasePath,$testPath -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$commonArgs = @('/nologo', '/optimize+', '/platform:x64', '/codepage:65001', '/warnaserror+',
    '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
    ('/r:' + (Join-Path $frameworkPath 'WPF\UIAutomationClient.dll')),
    ('/r:' + (Join-Path $frameworkPath 'WPF\UIAutomationTypes.dll')),
    ('/r:' + (Join-Path $frameworkPath 'WPF\WindowsBase.dll')),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')))
$appConfig = @('<?xml version="1.0" encoding="utf-8"?>', '<configuration>',
    '  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup>', '</configuration>') -join [Environment]::NewLine

$appExe = Join-Path $releasePath 'ASIORateMonitor.exe'
& $compilerPath @commonArgs /target:winexe ('/out:' + $appExe) @sources
if ($LASTEXITCODE -ne 0) { throw 'App build failed. Exit the running app before rebuilding.' }
Set-Content -LiteralPath ($appExe + '.config') -Value $appConfig -Encoding UTF8

$testExe = Join-Path $testPath 'RateTests.exe'
& $compilerPath @commonArgs /target:exe /main:AsioRateMonitor.SelfTests ('/out:' + $testExe) @sources (Join-Path $projectRoot 'tests\SelfTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Tests build failed.' }
Set-Content -LiteralPath ($testExe + '.config') -Value $appConfig -Encoding UTF8
& $testExe $appExe
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

$assemblyInfo = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AssemblyInfo.cs') -Raw
$versionMatch = [regex]::Match($assemblyInfo, 'AssemblyVersion\("(\d+\.\d+\.\d+)\.\d+"\)')
if (-not $versionMatch.Success) { throw 'Assembly version was not found.' }
$version = $versionMatch.Groups[1].Value
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE') -Destination $releasePath
$packageFiles = @($appExe, ($appExe + '.config'), (Join-Path $releasePath 'README.md'), (Join-Path $releasePath 'LICENSE'), (Join-Path $releasePath 'NOTICE'))
Compress-Archive -LiteralPath $packageFiles -DestinationPath (Join-Path $releasePath "asio-rate-monitor-v$version-win-x64.zip") -Force
$sourceFiles = @('src','tests','README.md','LICENSE','NOTICE','build.ps1','app.manifest','.gitignore') |
    ForEach-Object { Join-Path $projectRoot $_ }
Compress-Archive -LiteralPath $sourceFiles -DestinationPath (Join-Path $releasePath "asio-rate-monitor-v$version-source.zip") -Force
Write-Output ('Built: ' + $appExe)
