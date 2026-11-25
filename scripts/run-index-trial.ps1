param([switch]$VerifyOnly)
$ErrorActionPreference = 'Stop'
$root = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/FileAssistant.App/bin/index-trial'
$required = @('FileAssistant.IndexTrial.exe', 'FileAssistant.IndexTrial.pri', 'FileAssistant.IndexTrial.runtimeconfig.json',
    'FileAssistant.IndexTrial.deps.json', 'FileAssistant.Core.dll', 'FileAssistant.Infrastructure.dll', 'FileAssistant.Indexing.dll',
    'FileAssistant.Indexing.Windows.dll', 'FileAssistant.SpaceScan.exe', 'FileAssistant.CleanupHost.exe', 'e_sqlite3.dll',
    'Engines/ffmpeg/bin/ffmpeg.exe', 'Assets/AppIcon.ico')
$builds = if (Test-Path -LiteralPath $root) {
    :candidate foreach ($dll in (Get-ChildItem -LiteralPath $root -Filter FileAssistant.IndexTrial.dll -File -Recurse)) {
        if ($dll.DirectoryName -notmatch '\\x64\\Debug\\net[\d.]+-windows[\d.]+\\win-x64$' -or $dll.Length -eq 0) { continue }
        $updated = $dll.LastWriteTimeUtc
        foreach ($name in $required) {
            $file = [IO.FileInfo]::new((Join-Path $dll.DirectoryName $name))
            if (-not $file.Exists -or $file.Length -eq 0) { continue candidate }
            if ($file.LastWriteTimeUtc -gt $updated) { $updated = $file.LastWriteTimeUtc }
        }
        [pscustomobject]@{ Path = Join-Path $dll.DirectoryName 'FileAssistant.IndexTrial.exe'; Updated = $updated }
    }
}
$latest = $builds | Sort-Object @{Expression='Updated'; Descending=$true}, Path | Select-Object -First 1
if (-not $latest) { throw '找不到完整的 x64 自建索引测试版，请先运行 FileAssistant\scripts\build-index-trial.ps1。' }
if ($VerifyOnly) { return $latest.Path }
$session = [Diagnostics.Process]::GetCurrentProcess().SessionId
$previous = Get-Process -Name FileAssistant.IndexTrial -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $session -and $_.Path -ne $latest.Path }
if ($previous) { throw '另一个自建索引测试版仍在运行。请先正常关闭该测试版，再打开新构建。' }
Start-Process -FilePath $latest.Path -WorkingDirectory (Split-Path -LiteralPath $latest.Path)