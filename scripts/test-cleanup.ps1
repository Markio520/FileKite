param([string]$HelperPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'initialize-dotnet.ps1')
$taskRepo = Split-Path -Parent $PSScriptRoot
if (-not $HelperPath) {
    $HelperPath = Join-Path $taskRepo 'src/FileAssistant.App/bin/cleanup/x64/Debug/net10.0-windows10.0.26100.0/win-x64/FileAssistant.CleanupHost.exe'
}
$taskHelper = (Resolve-Path -LiteralPath $HelperPath).Path
Push-Location $taskRepo
try {
    & $taskDotnet build tests/FileAssistant.Cleanup.Integration/FileAssistant.Cleanup.Integration.csproj --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Cleanup integration fixture did not build.' }
    $taskFixture = Join-Path $taskRepo 'tests/FileAssistant.Cleanup.Integration/bin/Debug/net10.0-windows10.0.26100.0/win-x64/FileAssistant.Cleanup.Integration.exe'
    $taskReport = Join-Path $taskRepo 'artifacts/cleanup-integration-result.txt'
    # The harness only uninstalls its own GUID-scoped HKCU fixture and deletes its own temporary files.
    $taskProcess = Start-Process -FilePath $taskFixture -ArgumentList ('"' + $taskHelper + '" "' + $taskReport + '"') -WindowStyle Hidden -PassThru
    $taskProcess.WaitForExit()
    Get-Content -LiteralPath $taskReport
    if ($taskProcess.ExitCode -ne 0) { throw "Cleanup integration failed ($($taskProcess.ExitCode)). See $taskReport" }
} finally { Pop-Location }
