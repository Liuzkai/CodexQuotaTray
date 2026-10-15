$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$name = 'CodexQuotaTray-v2.6.0-floating.1'
$build = Join-Path $root 'build\floating'
$package = Join-Path $build 'package'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $build, $package, $dist | Out-Null
$references = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll',
    'System.Windows.Forms.dll', 'System.Web.Extensions.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll'
)
$common = @('/nologo', '/codepage:65001', '/optimize+')
$common += @($references | ForEach-Object { '/reference:' + $_ })
$manifest = '/win32manifest:' + (Join-Path $root 'app.manifest')
$icon = Join-Path $root 'assets\CodexQuotaTray.ico'
$exe = Join-Path $package "$name.exe"
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' -File | Sort-Object Name)
& $compiler @common /target:winexe $manifest ('/win32icon:' + $icon) ('/out:' + $exe) @($sources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }

# Compile only the widget and its display dependencies into the isolated test harness.
# No application entry point, quota/session reader, completion monitor, or live preview.
$testSources = @('AppSettings.cs', 'Models.cs', 'ThemePalette.cs', 'TaskbarWidgetForm.cs',
    'QuotaPopupForm.cs', 'AcrylicEffect.cs', 'LayeredTextOverlay.cs', 'UpdateService.cs') |
    ForEach-Object { Join-Path $root ('src\' + $_) }
$testExe = Join-Path $build 'WidgetTests.exe'
& $compiler @common /target:exe $manifest ('/out:' + $testExe) @testSources (Join-Path $root 'tests\WidgetTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Widget test compilation failed.' }
$ErrorActionPreference = 'Continue'
try {
    $testOutput = & $testExe (Join-Path $build 'fixtures') 2>&1
    $testExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = 'Stop' }
$testOutput | Tee-Object -FilePath (Join-Path $package 'TEST-RESULTS.txt') | Write-Output
if ($testExit -ne 0) { throw "Widget tests failed: $testExit" }

$bytes = [IO.File]::ReadAllBytes($exe)
if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { throw 'Missing executable header.' }
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) { throw 'Invalid PE header.' }
if ([BitConverter]::ToUInt16($bytes, $pe + 24 + 68) -ne 2) { throw 'Not a Windows GUI executable.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne '2.6.0.0') { throw 'Unexpected file version.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$sourceCommit = & git -c ("safe.directory=" + $root.Replace('\', '/')) -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not identify source commit.' }
$sourceStatus = @(& git -c ("safe.directory=" + $root.Replace('\', '/')) -C $root status --porcelain)
$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$info = [ordered]@{
    application = 'CodexQuotaTray'
    version = '2.6.0'
    buildRevision = 'floating.1'
    repository = 'Liuzkai/CodexQuotaTray'
    sourceCommit = $sourceCommit
    sourceWorkingTreeDirty = ($sourceStatus.Count -gt 0)
    buildTimeUtc = [DateTime]::UtcNow.ToString('o')
    compiler = $compiler
    compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiler).FileVersion
    embeddedIconSha256 = (Get-FileHash -LiteralPath $icon -Algorithm SHA256).Hash.ToLowerInvariant()
    executableSha256 = $exeHash
    executableBytes = $bytes.Length
    verification = 'Full application compilation; isolated WidgetTests; PE GUI header and version. Main application never launched.'
    limitations = 'Multi-monitor topology and DPI transitions use synthetic geometry/messages; physical monitor hot-plug and real pointer crossing still require manual verification.'
    signing = 'Unsigned community fork build; not an official OpenAI or upstream release.'
}
[IO.File]::WriteAllText((Join-Path $package 'BUILDINFO.json'), ($info | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText((Join-Path $package 'SOURCE_COMMIT.txt'), ($sourceCommit + "`r`n"), $utf8)
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'), $icon, (Join-Path $root 'assets\ICON-PROVENANCE.txt') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'FLOATING-WIDGET.md') -Destination (Join-Path $package 'README.md')
$inputs = @($sources.FullName) + @($icon, (Join-Path $root 'app.manifest'),
    (Join-Path $root 'tests\WidgetTests.cs'), $PSCommandPath)
$hashes = @($inputs | ForEach-Object {
    (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Substring($root.Length + 1).Replace('\', '/')
})
[IO.File]::WriteAllLines((Join-Path $package 'SOURCE_SHA256SUMS.txt'), [string[]]$hashes, $utf8)
$hashes = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
})
[IO.File]::WriteAllLines((Join-Path $package 'SHA256SUMS.txt'), [string[]]$hashes, $utf8)
$zip = Join-Path $dist "$name-Windows.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal -Force
Copy-Item -LiteralPath $exe -Destination $dist
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllLines((Join-Path $dist "$name-SHA256SUMS.txt"), @(
    "$exeHash  $name.exe", "$zipHash  $name-Windows.zip"), $utf8)
Write-Output "EXE: $exe"
Write-Output "ZIP: $zip"
Write-Output "EXE_SHA256=$exeHash"
Write-Output "ZIP_SHA256=$zipHash"
