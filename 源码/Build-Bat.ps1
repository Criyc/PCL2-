$ErrorActionPreference = 'Stop'
$work = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
Push-Location -LiteralPath $work
try {
    & $compiler /nologo /target:exe /optimize+ /platform:anycpu /out:PclRestore.exe /reference:Mono.Cecil.dll PclRestore.cs ModpackPatch.cs CompDetailPatch.cs
    if ($LASTEXITCODE -ne 0) { throw 'Patcher compilation failed.' }
    & $compiler /nologo /target:exe /optimize+ /platform:anycpu /out:VerifyPatched.exe VerifyPatched.cs
    if ($LASTEXITCODE -ne 0) { throw 'Verifier compilation failed.' }
} finally { Pop-Location }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$package = Join-Path $work 'tools.zip'
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
$archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('PclRestore.exe', 'VerifyPatched.exe', 'Mono.Cecil.dll', 'Mono.Cecil-LICENSE.txt')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $work $name), $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$runner = [IO.File]::ReadAllText((Join-Path $work 'Runner.ps1'), [Text.Encoding]::UTF8).Replace('__PAYLOAD_SHA256__', $hash)
$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($package), [Base64FormattingOptions]::InsertLineBreaks)
$header = @'
@echo off
rem Distribution watermark: Cricy
setlocal DisableDelayedExpansion
chcp 65001 >nul
set "PCL_FIX_SELF=%~f0"
set "PCL_FIX_ARG1=%~1"
set "PCL_FIX_ARG2=%~2"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop';$t=[IO.File]::ReadAllText($env:PCL_FIX_SELF,[Text.Encoding]::UTF8);$m=[regex]::Match($t,'(?ms)^:__POWERSHELL__\r?\n(.*?)^:__PAYLOAD__\r?\n');if(!$m.Success){throw 'Embedded script missing'};& ([scriptblock]::Create($m.Groups[1].Value))"
set "PCL_FIX_RESULT=%errorlevel%"
if not "%PCL_FIX_NOPAUSE%"=="1" pause
endlocal & exit /b %PCL_FIX_RESULT%
:__POWERSHELL__
'@
$bat = $header.TrimEnd() + "`r`n" + $runner.TrimEnd() + "`r`n:__PAYLOAD__`r`n" + $base64 + "`r`n"
$path = Join-Path $work 'PCL2-一键恢复下载.bat'
[IO.File]::WriteAllText($path, ($bat -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($false)))
$restore = @'
@echo off
rem Distribution watermark: Cricy
setlocal DisableDelayedExpansion
chcp 65001 >nul
call "%~dp0PCL2-一键恢复下载.bat" /restore "%~1"
exit /b %errorlevel%
'@
[IO.File]::WriteAllText((Join-Path $work 'PCL2-还原原版.bat'), ($restore -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $path | Select-Object FullName, Length
