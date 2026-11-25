param(
    [Parameter(Mandatory)][string]$IndexDirectory,
    [Parameter(Mandatory)][string[]]$Roots,
    [Parameter(Mandatory)][string]$Report,
    [ValidateSet('fixture', 'cold-fixture', 'cached-fixture', 'cached-volume', 'cold-volumes', 'cold-search','cold-space','cold-space-authorized','cached-space','cached-space-changes')][string]$Mode = 'cached-volume',
    [int]$MemoryMiB = 384,
    [string]$ApplicationExecutable,
    [string]$ProbeDirectory
)
$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$platform = 'net10.0-windows10.0.26100.0/win-x64'
if (-not $ApplicationExecutable) { $ApplicationExecutable = & (Join-Path $project 'scripts/run-index-trial.ps1') -VerifyOnly }
$delivery = Split-Path -LiteralPath (Resolve-Path -LiteralPath $ApplicationExecutable).Path
$appName = [IO.Path]::GetFileNameWithoutExtension($ApplicationExecutable)
$probe = if ($ProbeDirectory) { $ProbeDirectory } else { Join-Path $PSScriptRoot ('bin/Debug/' + $platform) }
$source = Join-Path $probe 'IndexTrialProbe.exe'
if (-not (Test-Path -LiteralPath $source)) { throw 'Build the full trial, then the UiProbe project first.' }
$indexPath = [IO.Path]::GetFullPath($IndexDirectory)
$reportPath = [IO.Path]::GetFullPath($Report)
$databases = @(Get-ChildItem -LiteralPath $indexPath -File -ErrorAction SilentlyContinue | Where-Object Extension -in '.sqlite3','.db')
if ($Mode -like 'cold-*' -and $databases.Count) {
    throw 'Use a fresh index directory for the cold run.'
}
if ($Mode -in 'cached-fixture','cached-space','cached-space-changes' -and -not $databases.Count) {
    throw 'No saved index for the warm run.'
}
$run = Join-Path $indexPath ('ui-probe-' + [Guid]::NewGuid().ToString('N'))
$runtime = [IO.Directory]::CreateDirectory((Join-Path $run 'app')).FullName
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $reportPath))
[IO.File]::WriteAllText($reportPath, '')
try {
    Copy-Item -Path (Join-Path $delivery '*') -Destination $runtime -Recurse
    foreach ($extension in @('exe','dll')) {
        Copy-Item -LiteralPath (Join-Path $probe "IndexTrialProbe.$extension") -Destination $runtime
    }
    foreach ($extension in @('pri','runtimeconfig.json','deps.json')) {
        Copy-Item -LiteralPath (Join-Path $delivery "$appName.$extension") -Destination (Join-Path $runtime "IndexTrialProbe.$extension")
    }
    $options = [Diagnostics.ProcessStartInfo]::new()
    $options.FileName = Join-Path $runtime 'IndexTrialProbe.exe'
    $options.WorkingDirectory = $runtime
    $options.UseShellExecute = $false
    $options.CreateNoWindow = $true
    $arguments = @($reportPath, $MemoryMiB.ToString(), $Mode, $indexPath, (Join-Path $run 'profile')) + $Roots
    $options.Arguments = ($arguments | ForEach-Object { '"' + [regex]::Replace($_, '(\\+)$', '$1$1') + '"' }) -join ' '
    $child = [Diagnostics.Process]::Start($options)
    try {
        $child.WaitForExit()
        if ($child.ExitCode -ne 0 -or (Get-Content -LiteralPath $reportPath -Tail 1) -ne 'PASS') {
            throw "Probe exited with $($child.ExitCode). See $reportPath"
        }
    } finally { $child.Dispose() }
} finally {
    $resolved = [IO.Path]::GetFullPath($run)
    if ([IO.Path]::GetDirectoryName($resolved) -eq $indexPath -and [IO.Path]::GetFileName($resolved) -match '^ui-probe-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Get-Content -LiteralPath $reportPath
