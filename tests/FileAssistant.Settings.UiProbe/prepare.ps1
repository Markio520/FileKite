param(
    [string]$Dotnet = 'dotnet',
    [string]$ReleaseTree = 'codex-settings-v6-20261121',
    [string]$RuntimeDirectory = (Join-Path $PSScriptRoot 'bin/codex-v6-batch/native-runtime'),
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ProbeBuildTag = 'codex-v6-batch'
)
$ErrorActionPreference = 'Stop'
$tfm = 'net10.0-windows10.0.26100.0/win-x64'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$release = Join-Path $project "src/FileAssistant.App/bin/$ReleaseTree/x64/Release/$tfm"
if (!(Test-Path -LiteralPath (Join-Path $release 'FileAssistant.App.pri'))) { throw 'Build the requested app Release tree first.' }
& $Dotnet build (Join-Path $PSScriptRoot 'FileAssistant.Settings.UiProbe.csproj') -c Release "-p:ReleaseTree=$ReleaseTree" "-p:BaseOutputPath=bin/$ProbeBuildTag/" "-p:BaseIntermediateOutputPath=obj/$ProbeBuildTag/" -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Settings UI probe compilation failed.' }
$runtime = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($RuntimeDirectory)).FullName
$built = Join-Path $PSScriptRoot "bin/$ProbeBuildTag/Release/$tfm"
& robocopy $release $runtime /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "Could not stage the workbench (robocopy $LASTEXITCODE)." }
$name = 'FileAssistant.Settings.UiProbe'
foreach ($extension in @('exe', 'dll')) {
    Copy-Item -LiteralPath (Join-Path $built "$name.$extension") -Destination $runtime -Force
}
foreach ($extension in @('pri', 'runtimeconfig.json', 'deps.json')) {
    Copy-Item -LiteralPath (Join-Path $release "FileAssistant.App.$extension") -Destination (Join-Path $runtime "$name.$extension") -Force
}
Write-Output (Join-Path $runtime "$name.exe")
exit 0
