param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent $PSScriptRoot
$taskBin = Join-Path $taskRepo 'src\FileAssistant.App\bin'
$requiredFiles = @(
    'FileAssistant.App.dll', 'FileAssistant.Core.dll', 'FileAssistant.Infrastructure.dll',
    'FileAssistant.App.pri', 'FileAssistant.App.deps.json', 'FileAssistant.App.runtimeconfig.json'
)

$builds = if ([IO.Directory]::Exists($taskBin)) {
    :build foreach ($exe in (Get-ChildItem -LiteralPath $taskBin -Filter 'FileAssistant.App.exe' -Recurse -File)) {
        $relativePath = $exe.FullName.Substring($taskBin.Length + 1)
        if ($exe.Length -eq 0 -or $relativePath -match '(^|\\)(candidates|packaged|AppX|AppPackages)(\\|$)' -or
            [IO.File]::Exists((Join-Path $exe.DirectoryName 'AppxManifest.xml'))) { continue }

        # Incremental builds can keep the old EXE while updating assemblies or XAML resources.
        $updatedUtc = $exe.LastWriteTimeUtc
        foreach ($name in $requiredFiles) {
            $file = [IO.FileInfo]::new((Join-Path $exe.DirectoryName $name))
            if (-not $file.Exists -or $file.Length -eq 0) { continue build }
            if ($file.LastWriteTimeUtc -gt $updatedUtc) { $updatedUtc = $file.LastWriteTimeUtc }
        }
        $deps = Join-Path $exe.DirectoryName 'FileAssistant.App.deps.json'
        if (Select-String -LiteralPath $deps -SimpleMatch '"FileAssistant.Indexing.Windows/' -Quiet) {
            if (Test-Path -LiteralPath (Join-Path $exe.DirectoryName 'SearchEngine/Everything.exe')) { continue }
            foreach ($name in @('FileAssistant.Indexing.Windows.dll','FileAssistant.Indexing.dll','e_sqlite3.dll',
                'Microsoft.Data.Sqlite.dll','FileAssistant.SpaceScan.exe','FileAssistant.CleanupHost.exe','Engines/ffmpeg/bin/ffmpeg.exe')) {
                $file = [IO.FileInfo]::new((Join-Path $exe.DirectoryName $name))
                if (-not $file.Exists -or $file.Length -eq 0) { continue build }
            }
        }
        [pscustomobject]@{ Executable = $exe.FullName; UpdatedUtc = $updatedUtc; IsRelease = $relativePath -match '(^|\\)Release(\\|$)' }
    }
}

# Independent Debug verifications must not replace the latest complete Release delivered to the user.
$latest = $builds | Sort-Object @{ Expression = 'IsRelease'; Descending = $true }, @{ Expression = 'UpdatedUtc'; Descending = $true }, Executable | Select-Object -First 1
if (-not $latest) { throw '找不到可直接运行的纸鸢 · FileKite。请先完成一次非打包构建，再双击启动。' }
if ($VerifyOnly) { return $latest.Executable }

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$otherVersion = Get-Process -Name FileAssistant.App -ErrorAction SilentlyContinue | Where-Object {
    $_.SessionId -eq $sessionId -and $_.Threads.Count -gt 0 -and $_.Path -ne $latest.Executable
}
if ($otherVersion) {
    throw '另一个版本的纸鸢 · FileKite正在运行。请正常关闭旧窗口，让程序保存列表与分组，再双击启动最新版。'
}

# When this version is already open, its single-instance handler activates the existing window.
Start-Process -FilePath $latest.Executable -WorkingDirectory (Split-Path -LiteralPath $latest.Executable)
