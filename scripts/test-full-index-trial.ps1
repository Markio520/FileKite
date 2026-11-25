param(
    [Parameter(Mandatory)][ValidateCount(2,2)][string[]]$FixtureParents,
    [string]$EverythingExecutable,
    [string]$ApplicationExecutable,
    [string]$ApplicationManifest,
    [string]$LegacyApplicationExecutable,
    [string]$PreviousSharedExecutable,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')][string]$BuildTag,
    [switch]$WholeVolumes,
    [ValidateRange(0,5)][int]$SearchRepeats = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'initialize-dotnet.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$parents = @($FixtureParents | ForEach-Object { (Get-Item -LiteralPath $_).FullName })
$volumes = @($parents | ForEach-Object { [IO.Path]::GetPathRoot($_) } | Select-Object -Unique)
if ($volumes.Count -ne 2) { throw '请选择分属两个不同卷的可写目录。' }
$token = [Guid]::NewGuid().ToString('N')
$scratch = [IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) "FileAssistant-index-probe-$token")).FullName
$created = @()
$report = [IO.Directory]::CreateDirectory((Join-Path $repo "artifacts/full-index-trial-$token")).FullName
$originalEngine = $env:FILEASSISTANT_TEST_ENGINE
Push-Location $repo
try {
    if (-not $ApplicationExecutable) {
        & (Join-Path $PSScriptRoot 'build-index-trial.ps1')
        $ApplicationExecutable = & (Join-Path $PSScriptRoot 'run-index-trial.ps1') -VerifyOnly
    }
    $ApplicationExecutable = (Get-Item -LiteralPath $ApplicationExecutable).FullName
    $app = Join-Path $repo 'src/FileAssistant.App'
    if (-not $ApplicationManifest) {
        $bin = [IO.Path]::GetFullPath((Join-Path $app 'bin'))
        $configuration = (Split-Path -LiteralPath $ApplicationExecutable).Substring($bin.Length + 1)
        $ApplicationManifest = Join-Path $app ("obj/$configuration/Manifests/app.manifest")
    }
    $manifest = (Get-Item -LiteralPath $ApplicationManifest).FullName
    $workbenches = [ordered]@{ shared = $ApplicationExecutable }
    if ($LegacyApplicationExecutable) {
        $workbenches.legacy = (Get-Item -LiteralPath $LegacyApplicationExecutable).FullName
        if (-not $EverythingExecutable) {
            $EverythingExecutable = Join-Path (Split-Path -LiteralPath $workbenches.legacy) 'SearchEngine/Everything.exe'
        }
    }
    $probes = @{}
    foreach ($kind in $workbenches.Keys) {
        $executable = $workbenches[$kind]
        $probes[$kind] = @{
            ApplicationExecutable = $executable
            ProbeDirectory = Join-Path $scratch "probe-$kind"
        }
        $arguments = @("-p:TrialBuild=$(Split-Path -LiteralPath $executable)", "-p:WorkbenchAssembly=$([IO.Path]::GetFileNameWithoutExtension($executable))",
            "-p:ApplicationManifest=$manifest", "-p:LegacyWorkbench=$($kind -eq 'legacy')", "-p:OutDir=$($probes[$kind].ProbeDirectory)",
            "-p:BaseIntermediateOutputPath=$scratch/obj-$kind/")
        & $taskDotnet build tests/FileAssistant.IndexTrial.UiProbe/FileAssistant.IndexTrial.UiProbe.csproj @arguments --nologo
        if ($LASTEXITCODE) { throw "界面验收程序构建失败 ($kind)。" }
    }
    $benchOutput = if ($BuildTag) { "bin/candidates/$BuildTag" } else { 'bin' }
    $buildPaths = if ($BuildTag) { @("-p:BaseOutputPath=$benchOutput/", "-p:BaseIntermediateOutputPath=obj/$BuildTag/") } else { @() }
    & $taskDotnet build tests/FileAssistant.IndexBench/FileAssistant.IndexBench.csproj -c Release @buildPaths --nologo
    if ($LASTEXITCODE) { throw '索引验收程序构建失败。' }
    & $taskDotnet test tests/FileAssistant.Indexing.Tests/FileAssistant.Indexing.Tests.csproj @buildPaths --nologo
    if ($LASTEXITCODE) { throw '索引回归失败。' }
    $bench = Join-Path $repo "tests/FileAssistant.IndexBench/$benchOutput/Release/net10.0-windows10.0.26100.0/FileAssistant.IndexBench.dll"
    $ui = Join-Path $repo 'tests/FileAssistant.IndexTrial.UiProbe/run.ps1'
    foreach ($v in 0..1) {
        $fixture = Join-Path $parents[$v] "FileAssistant-index-fixture-$token"
        if ([IO.Directory]::Exists($fixture)) { throw '样本目录已存在。' }
        $created += [IO.Directory]::CreateDirectory($fixture).FullName
        foreach ($n in 0..7) { [void][IO.Directory]::CreateDirectory((Join-Path $fixture "目录$v-$n")) }
        foreach ($i in 0..479) {
            $id = $v * 480 + $i
            $pattern = @('项目素材{0:D5}.mp4','产品{0:D5}.png','报价{0:D5}.pdf','说明{0:D5}.txt')[$id % 4]
            $file = Join-Path $fixture ("目录$v-" + ($i % 8) + '/' + ($pattern -f $id))
            [IO.File]::WriteAllBytes($file,[byte[]]::new(1024 + $id))
        }
    }
    $db = Join-Path $scratch 'fixtures'
    & $taskDotnet $bench trial-real 256 $db @created > (Join-Path $report 'indexed.json')
    if ($LASTEXITCODE) { throw '跨盘查询验收失败。' }
    if ($EverythingExecutable) {
        $env:FILEASSISTANT_TEST_ENGINE = (Resolve-Path -LiteralPath $EverythingExecutable).Path
        & $taskDotnet $bench trial-legacy 256 (Join-Path $scratch 'comparison') @created > (Join-Path $report 'comparison.json')
        if ($LASTEXITCODE) { throw '隔离的 Everything 对照失败。' }
        $a = (Get-Content -Raw -LiteralPath (Join-Path $report 'indexed.json') | ConvertFrom-Json).outputs
        $b = (Get-Content -Raw -LiteralPath (Join-Path $report 'comparison.json') | ConvertFrom-Json).outputs
        for ($i=0; $i -lt $a.Count; $i++) {
            if ($a[$i].hits -ne $b[$i].hits -or $a[$i].signature -cne $b[$i].signature) {
                throw ('查询结果不一致 ' + $a[$i].Text)
            }
        }
    }
    $shared = $probes.shared
    & $ui @shared -Mode fixture -IndexDirectory $db -Roots $created -Report (Join-Path $report 'ui-fixture.log') -MemoryMiB 256
    if ($PreviousSharedExecutable) {
        $previous = (Get-Item -LiteralPath $PreviousSharedExecutable).FullName
        $upgradeCache = Join-Path $scratch 'upgrade'
        $beforeLog = Join-Path $report 'ui-upgrade-before.log'
        $afterLog = Join-Path $report 'ui-upgrade-after.log'
        & $ui -ApplicationExecutable $previous -ProbeDirectory $shared.ProbeDirectory -Mode cold-fixture -IndexDirectory $upgradeCache -Roots $created -Report $beforeLog -MemoryMiB 256
        $original = Join-Path $created[0] '目录0-1/产品00001.png'
        $renamed = Join-Path $created[0] '目录0-1/离线改名00001.png'
        [IO.File]::Move($original, $renamed)
        try {
            & $ui @shared -Mode cached-fixture -IndexDirectory $upgradeCache -Roots $created -Report $afterLog -MemoryMiB 256
            $before = (Get-Content -LiteralPath $beforeLog -Encoding UTF8 -TotalCount 1 | ConvertFrom-Json).generations
            $after = (Get-Content -LiteralPath $afterLog -Encoding UTF8 -TotalCount 1 | ConvertFrom-Json).generations
            $untouched = ($before | Where-Object root -eq $created[1]).generation
            if (-not $untouched -or $untouched -cne ($after | Where-Object root -eq $created[1]).generation) { throw '升级时未复用未变位置的原有索引。' }
        } finally { [IO.File]::Move($renamed, $original) }
    }
    if ($LegacyApplicationExecutable) {
        $measurements = foreach ($mode in 'cold-fixture','cached-fixture') {
            if ($mode -eq 'cached-fixture') {
                $folder = Join-Path $created[0] '目录0-1'
                [IO.File]::Move((Join-Path $folder '产品00001.png'), (Join-Path $folder '离线改名00001.png'))
            }
            foreach ($kind in $workbenches.Keys) {
                $probe = $probes[$kind]
                $cache = Join-Path $scratch "ui-cache-$kind"
                $log = Join-Path $report "ui-$kind-$mode.log"
                & $ui @probe -Mode $mode -IndexDirectory $cache -Roots $created -Report $log -MemoryMiB 256 | Out-Host
                $reading = Get-Content -LiteralPath $log -TotalCount 1 -Encoding UTF8 | ConvertFrom-Json
                $disk = Get-ChildItem -LiteralPath $cache -File | Where-Object Name -Match '\.(sqlite3(-wal|-shm)?|db)$' | Measure-Object -Property Length -Sum
                if (-not $disk.Sum) { throw "未找到已保存索引 ($kind)。" }
                [pscustomobject]@{ engine=$kind; mode=$mode; firstPageMs=$reading.firstQueryMs; readyQueryMs=$reading.repeatedQueryMs;
                    groupPeakBytes=($reading.samples | Measure-Object peakGroupPrivate -Maximum).Maximum; indexBytes=$disk.Sum;
                    hits=$reading.searchHits; entries=$reading.entries; types=$reading.types; logicalBytes=$reading.logicalBytes; countedBytes=$reading.bytes }
            }
        }
        $measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report 'workbench-comparison.json') -Encoding UTF8
        foreach ($field in 'hits','entries','types','logicalBytes','countedBytes') {
            if (@($measurements.$field | Select-Object -Unique).Count -ne 1) { throw "完整工作台结果不一致 ($field)。" }
        }
    }
    if ($WholeVolumes) {
        $db = Join-Path $scratch 'volumes'
        & $ui @shared -Mode cold-volumes -IndexDirectory $db -Roots $volumes -Report (Join-Path $report 'ui-cold.log') -MemoryMiB 256
        & $ui @shared -Mode cached-volume -IndexDirectory $db -Roots $volumes -Report (Join-Path $report 'ui-warm.log') -MemoryMiB 256
        Get-ChildItem -LiteralPath $db -File -Filter '*.sqlite3*' | Select-Object Name,Length |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report 'volume-index-sizes.json') -Encoding UTF8
        for ($i = 1; $i -le $SearchRepeats; $i++) {
            & $ui @shared -Mode cold-search -IndexDirectory (Join-Path $scratch "names-$i") -Roots $volumes -Report (Join-Path $report "ui-search-$i.log") -MemoryMiB 256
        }
    }
    Write-Output $report
} finally {
    $env:FILEASSISTANT_TEST_ENGINE = $originalEngine
    foreach ($path in @($created) + @($scratch)) {
        $target = [IO.Path]::GetFullPath($path)
        $parentPath = [IO.Path]::GetDirectoryName($target)
        $isFixture = [IO.Path]::GetFileName($target) -ceq "FileAssistant-index-fixture-$token" -and
            @($parents | Where-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') -eq $parentPath.TrimEnd('\') }).Count -eq 1
        $isScratch = [IO.Path]::GetFileName($target) -ceq "FileAssistant-index-probe-$token" -and
            $parentPath.TrimEnd('\') -eq [IO.Path]::GetTempPath().TrimEnd('\')
        if ($isFixture -or $isScratch) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
    Pop-Location
}
