param([Parameter(Mandatory=$true)][string]$OriginalExe)
$ErrorActionPreference = 'Stop'
$work = $PSScriptRoot
$root = Join-Path $work ('portable-tests-' + [Guid]::NewGuid().ToString('N'))
$case = Join-Path $root '目录 with spaces & !'
New-Item -ItemType Directory -Path $case -Force | Out-Null
$target = Join-Path $case 'Plain Craft Launcher 2.exe'
$bat = Join-Path $case 'PCL2-一键恢复下载.bat'
$restoreBat = Join-Path $case 'PCL2-还原原版.bat'
Copy-Item -LiteralPath (Join-Path $work 'PCL2-一键恢复下载.bat') -Destination $bat
Copy-Item -LiteralPath (Join-Path $work 'PCL2-还原原版.bat') -Destination $restoreBat
# Vary the harmless PE timestamp to verify that users are not bound to one EXE hash.
$bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($OriginalExe))
$peOffset = [BitConverter]::ToInt32($bytes, 60)
$timestamp = [BitConverter]::ToUInt32($bytes, $peOffset + 8)
$changed = [BitConverter]::GetBytes([uint32]($timestamp + 1))
[Array]::Copy($changed, 0, $bytes, $peOffset + 8, 4)
[IO.File]::WriteAllBytes($target, $bytes)
$before = (Get-FileHash -LiteralPath $target).Hash
$realHash = (Get-FileHash -LiteralPath $OriginalExe).Hash
if ($before -eq $realHash) { throw 'Variant fixture was not generated.' }
New-Item -ItemType Directory -Path (Join-Path $case 'PCL') | Out-Null
[IO.File]::WriteAllText((Join-Path $case 'PCL\Setup.ini'), "Fixture:preserve`r`n", [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $case 'PCL.ini'), "Fixture:root`r`n", [Text.Encoding]::UTF8)
$configBefore = (Get-FileHash -LiteralPath (Join-Path $case 'PCL\Setup.ini')).Hash
$env:PCL_FIX_NOPAUSE = '1'
$env:PCL_FIX_NO_LAUNCH = '1'
try {
    & $bat
    if ($LASTEXITCODE -ne 0) { throw 'Portable default-location patch failed.' }
    $after = (Get-FileHash -LiteralPath $target).Hash
    if ($after -eq $before) { throw 'Executable was not patched.' }
    if ((Get-FileHash -LiteralPath (Join-Path $case 'PCL\Setup.ini')).Hash -ne $configBefore) { throw 'Configuration changed.' }
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $case 'PCL-download-backups') -Directory)
    if ($backups.Count -ne 1) { throw 'Expected exactly one backup.' }
    if ((Get-FileHash -LiteralPath (Join-Path $backups[0].FullName 'original.exe')).Hash -ne $before) { throw 'Backup mismatch.' }
    Write-Host 'TEST_OK: portable path with Chinese, spaces, ampersand and exclamation; different EXE hash; backup; settings preserved.'
    & $bat $target
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $target).Hash -ne $after) { throw 'Repeated patch not idempotent.' }
    if (@(Get-ChildItem -LiteralPath (Join-Path $case 'PCL-download-backups') -Directory).Count -ne 1) { throw 'Repeated patch created backup.' }
    Write-Host 'TEST_OK: idempotent second run.'

    $moved = Join-Path $root 'relocated'
    Copy-Item -LiteralPath $case -Destination $moved -Recurse
    & (Join-Path $moved 'PCL2-还原原版.bat')
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $moved 'Plain Craft Launcher 2.exe')).Hash -ne $before) {
        throw 'Portable restore after relocation failed.'
    }
    Write-Host 'TEST_OK: restoration works after relocating the launcher directory.'
    & $restoreBat
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $target).Hash -ne $before) { throw 'Original restoration failed.' }
    & $restoreBat
    if ($LASTEXITCODE -ne 0) { throw 'Repeated restore failed.' }
    Write-Host 'TEST_OK: byte-for-byte restoration and repeated restoration.'

    $unsupported = Join-Path $case 'unsupported.exe'
    [IO.File]::WriteAllText($unsupported, 'This is not a managed PCL executable.')
    $unsupportedHash = (Get-FileHash -LiteralPath $unsupported).Hash
    & $bat $unsupported
    if ($LASTEXITCODE -eq 0 -or (Get-FileHash -LiteralPath $unsupported).Hash -ne $unsupportedHash) { throw 'Unsupported file handling failed.' }
    Write-Host 'TEST_OK: incompatible file rejected without modification.'
    if ((Get-FileHash -LiteralPath $OriginalExe).Hash -ne $realHash) { throw 'Real launcher changed during tests.' }
    Write-Host "ALL_BAT_TESTS_PASSED: $root"
} finally {
    Remove-Item Env:PCL_FIX_NOPAUSE -ErrorAction SilentlyContinue
    Remove-Item Env:PCL_FIX_NO_LAUNCH -ErrorAction SilentlyContinue
}
