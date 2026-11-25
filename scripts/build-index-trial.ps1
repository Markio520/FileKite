$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'initialize-dotnet.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$arguments = @('build', (Join-Path $repo 'src/FileAssistant.App/FileAssistant.App.csproj'), '-c', 'Debug', '-p:Platform=x64',
    '-p:WindowsPackageType=None', '-p:SelfContained=true', '-p:WindowsAppSDKSelfContained=true', '-p:SharedIndexTrial=true', '--nologo')
if (Get-Process -Name FileAssistant.IndexTrial -ErrorAction SilentlyContinue) {
    $arguments += '-p:IndexTrialBuild=' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
}
Push-Location $repo
try {
    & $taskDotnet @arguments
    if ($LASTEXITCODE) { throw '自建索引测试版构建失败。' }
    & (Join-Path $PSScriptRoot 'run-index-trial.ps1') -VerifyOnly
} finally { Pop-Location }