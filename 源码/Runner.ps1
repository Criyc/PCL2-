$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$payloadHash = '__PAYLOAD_SHA256__'
$tempRoot = $null
$stage = $null
$exitCode = 0

function Get-Sha([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Get-TargetProcesses([string]$Path) {
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and [string]::Equals($_.Path, $Path, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}
function Close-Target([string]$Path) {
    $running = @(Get-TargetProcesses $Path)
    foreach ($process in $running) {
        if (!$process.HasExited) {
            Write-Host '正在请求 PCL 正常退出，以保存设置……'
            if (!$process.CloseMainWindow() -or !$process.WaitForExit(15000)) {
                throw 'PCL 未正常退出。请关闭 PCL 后重新运行；脚本没有强制结束进程。'
            }
        }
    }
    return ($running.Count -gt 0)
}

try {
    Write-Host ''
    Write-Host 'PCL 2.99.0.0 本地下载功能恢复工具' -ForegroundColor Cyan
    Write-Host '自动定位或选择启动器；校验版本与结构；备份后恢复下载功能。'
    $self = [IO.Path]::GetFullPath($env:PCL_FIX_SELF)
    $first = $env:PCL_FIX_ARG1
    $second = $env:PCL_FIX_ARG2
    $mode = 'patch'
    $requested = $null
    if ($first -in @('/restore', '--restore')) { $mode = 'restore'; $requested = $second }
    elseif ($first -in @('/check', '--check')) { $mode = 'check'; $requested = $second }
    else {
        $requested = $first
        if ($second -in @('/restore', '--restore')) { $mode = 'restore' }
        elseif ($second -in @('/check', '--check')) { $mode = 'check' }
    }
    $target = $null
    if ($requested) {
        $target = [IO.Path]::GetFullPath($requested)
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "找不到指定启动器：$target" }
    } else {
        $folder = Split-Path -LiteralPath $self
        foreach ($name in @('Plain Craft Launcher 2.exe', 'PCL2.exe', 'PCL.exe')) {
            $candidate = Join-Path $folder $name
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $target = $candidate; break }
        }
        if (!$target) {
            $running = @(Get-Process -Name 'Plain Craft Launcher 2' -ErrorAction SilentlyContinue)
            $paths = @($running | ForEach-Object { try { $_.Path } catch {} } | Where-Object { $_ } | Sort-Object -Unique)
            if ($paths.Count -eq 1) { $target = $paths[0] }
        }
        if (!$target) {
            Add-Type -AssemblyName System.Windows.Forms
            $dialog = New-Object System.Windows.Forms.OpenFileDialog
            try {
                $dialog.Title = '请选择 PCL 2.99.0.0 启动器'
                $dialog.Filter = 'PCL 启动器 (*.exe)|*.exe'
                if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { throw '已取消选择，未修改文件。' }
                $target = $dialog.FileName
            } finally { $dialog.Dispose() }
        }
    }
    $target = [IO.Path]::GetFullPath($target)
    $targetDir = Split-Path -LiteralPath $target
    Write-Host "启动器：$target"
    $currentHash = Get-Sha $target
    $backupRoot = Join-Path $targetDir 'PCL-download-backups'

    if ($mode -eq 'restore') {
        $backups = @()
        if (Test-Path -LiteralPath $backupRoot) {
            $backups = @(Get-ChildItem -LiteralPath $backupRoot -Directory | Sort-Object Name -Descending)
        }
        $backup = $null
        $alreadyRestored = $false
        foreach ($entry in $backups) {
            $manifestFile = Join-Path $entry.FullName 'manifest.json'
            $original = Join-Path $entry.FullName 'original.exe'
            if (!(Test-Path -LiteralPath $manifestFile) -or !(Test-Path -LiteralPath $original)) { continue }
            try {
                $manifest = Get-Content -LiteralPath $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
                if ((Get-Sha $original) -ne $manifest.OriginalSHA256) { continue }
                # Hashes allow the whole launcher folder to be moved after patching.
                if ($currentHash -eq $manifest.OriginalSHA256) { $alreadyRestored = $true }
                if ($currentHash -eq $manifest.PatchedSHA256) {
                    $backup = $original
                    $originalHash = $manifest.OriginalSHA256
                    $patchedHash = $manifest.PatchedSHA256
                    break
                }
            } catch { continue }
        }
        if ($alreadyRestored -and !$backup) {
            Write-Host '当前已还原，无需重复操作。' -ForegroundColor Green
        } else {
            if (!$backup) { throw '未找到与当前启动器哈希匹配的有效备份；没有覆盖任何文件。' }
            $stage = Join-Path $targetDir ('.pcl-restore-' + [Guid]::NewGuid().ToString('N') + '.exe')
            Copy-Item -LiteralPath $backup -Destination $stage
            if ((Get-Sha $stage) -ne $originalHash) { throw '还原文件校验失败。' }
            $wasRunning = Close-Target $target
            if ((Get-Sha $target) -ne $patchedHash) { throw '启动器在操作期间发生变化，已停止。' }
            [IO.File]::Replace($stage, $target, [NullString]::Value)
            $stage = $null
            if ((Get-Sha $target) -ne $originalHash) { throw "还原校验失败。原版备份位于：$backup" }
            Write-Host 'RESTORE_OK：已还原官方原版。' -ForegroundColor Green
            if ($wasRunning -and $env:PCL_FIX_NO_LAUNCH -ne '1') {
                Start-Process -FilePath $target -WorkingDirectory $targetDir | Out-Null
            }
        }
    } else {
        $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $tempRoot = Join-Path $tempBase ('PCL2DownloadRestore-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
        $fullText = [IO.File]::ReadAllText($self, [Text.Encoding]::UTF8)
        $payloadMatch = [regex]::Match($fullText, '(?ms)^:__PAYLOAD__\r?\n(.*?)\s*\z')
        if (!$payloadMatch.Success) { throw '内嵌工具包不存在或损坏。' }
        $zip = Join-Path $tempRoot 'tools.zip'
        [IO.File]::WriteAllBytes($zip, [Convert]::FromBase64String($payloadMatch.Groups[1].Value))
        $actualPayloadHash = Get-Sha $zip
        if ($actualPayloadHash -ne $payloadHash) { throw "内嵌工具包 SHA-256 校验失败：$actualPayloadHash / $payloadHash" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $tempRoot)
        & (Join-Path $tempRoot 'PclRestore.exe') --inspect $target | Out-Null
        $inspectResult = $LASTEXITCODE
        if ($inspectResult -eq 10) {
            Write-Host 'ALREADY_PATCHED：已恢复下载功能，无需重复修改。' -ForegroundColor Green
            exit 0
        }
        if ($inspectResult -ne 0) { throw '此启动器版本或程序结构不受支持；没有修改文件。当前适配 PCL 2.99.0.0。' }
        $originalHash = $currentHash
        $patched = Join-Path $tempRoot 'patched.exe'
        Write-Host '正在恢复下载、整合包安装及版本修改入口……'
        & (Join-Path $tempRoot 'PclRestore.exe') $target $patched | Out-Null
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $patched)) { throw '补丁生成失败，原启动器未修改。' }
        $patchedHash = Get-Sha $patched
        if ($patchedHash -eq $originalHash) { throw '补丁没有产生有效修改。' }
        Write-Host '正在校验修复结果……'
        & (Join-Path $tempRoot 'VerifyPatched.exe') $patched | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '.NET 方法验证失败，原启动器未修改。' }
        if ($mode -eq 'check') {
            Write-Host 'CHECK_OK：补丁生成和验证通过；检查模式未修改启动器。' -ForegroundColor Green
        } else {
            $stage = Join-Path $targetDir ('.pcl-restore-' + [Guid]::NewGuid().ToString('N') + '.exe')
            Copy-Item -LiteralPath $patched -Destination $stage
            if ((Get-Sha $stage) -ne $patchedHash) { throw '待安装文件校验失败。' }
            $wasRunning = Close-Target $target
            if ((Get-Sha $target) -ne $originalHash) { throw '启动器在操作期间发生变化，已停止。' }
            $backupDir = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
            New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
            $original = Join-Path $backupDir 'original.exe'
            Copy-Item -LiteralPath $target -Destination $original
            if ((Get-Sha $original) -ne $originalHash) { throw '原版备份校验失败，已停止。' }
            $settings = @()
            foreach ($relative in @('PCL.ini', 'PCL\Setup.ini')) {
                $config = Join-Path $targetDir $relative
                if (Test-Path -LiteralPath $config -PathType Leaf) {
                    $savedName = $relative.Replace('\', '-')
                    Copy-Item -LiteralPath $config -Destination (Join-Path $backupDir $savedName)
                    $settings += @{ Path = $relative; Backup = $savedName; SHA256 = (Get-Sha (Join-Path $backupDir $savedName)) }
                }
            }
            @{
                Target = $target
                Created = (Get-Date).ToString('o')
                Version = '2.99.0.0'
                OriginalSHA256 = $originalHash
                PatchedSHA256 = $patchedHash
                Settings = $settings
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupDir 'manifest.json') -Encoding UTF8
            [IO.File]::Replace($stage, $target, [NullString]::Value)
            $stage = $null
            if ((Get-Sha $target) -ne $patchedHash) {
                $stage = Join-Path $targetDir ('.pcl-restore-' + [Guid]::NewGuid().ToString('N') + '.exe')
                Copy-Item -LiteralPath $original -Destination $stage
                [IO.File]::Replace($stage, $target, [NullString]::Value)
                $stage = $null
                throw '安装后校验异常，已回滚到备份原版。'
            }
            Write-Host 'SUCCESS：下载功能已恢复。' -ForegroundColor Green
            Write-Host "原版与配置备份：$backupDir"
            Write-Host '需要撤销时，运行配套的“PCL2-还原原版.bat”。'
            if ($wasRunning -and $env:PCL_FIX_NO_LAUNCH -ne '1') {
                Start-Process -FilePath $target -WorkingDirectory $targetDir | Out-Null
            }
        }
    }
} catch {
    Write-Host ''
    Write-Host ('FAILED：' + $_.Exception.Message) -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        # Only the exact temporary file allocated by this script is removed.
        Remove-Item -LiteralPath $stage -Force -ErrorAction SilentlyContinue
    }
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
        $allowedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedTemp.StartsWith($allowedParent, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedTemp) -match '^PCL2DownloadRestore-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
exit $exitCode
