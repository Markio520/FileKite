param([switch]$Benchmark)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'initialize-dotnet.ps1')
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    & $taskDotnet build src/FileAssistant.IndexLab/FileAssistant.IndexLab.csproj -c Debug -p:Platform=x64 --nologo
    if ($LASTEXITCODE) { throw 'Index lab build failed.' }
    & $taskDotnet test tests/FileAssistant.Indexing.Tests/FileAssistant.Indexing.Tests.csproj --nologo
    if ($LASTEXITCODE) { throw 'Index regressions failed.' }
    if (-not $Benchmark) { return }

    & $taskDotnet build tests/FileAssistant.IndexBench/FileAssistant.IndexBench.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw 'Index benchmark build failed.' }
    $bench = Join-Path $repo 'tests/FileAssistant.IndexBench/bin/Release/net10.0-windows10.0.26100.0/FileAssistant.IndexBench.dll'
    $run = Join-Path $repo ('artifacts/indexlab-measure-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($run)
    $wire = Join-Path $run 'sample.bin'
    & $taskDotnet $bench fixture $wire
    if ($LASTEXITCODE) { throw 'Generating benchmark input failed.' }
    foreach ($round in 1..3) {
        $modes = if ($round -eq 2) { @('indexed','space','folder-search') } else { @('space','folder-search','indexed') }
        $database = Join-Path $run "database-$round"
        foreach ($mode in $modes) {
            $result = & $taskDotnet $bench $mode $wire $database
            if ($LASTEXITCODE) { throw "Benchmark failed for $mode." }
            $result | Set-Content -LiteralPath (Join-Path $run "$mode-$round.json") -Encoding utf8
        }
        $reopen = & $taskDotnet $bench reopen $database
        if ($LASTEXITCODE) { throw 'Reopening the database failed.' }
        $reopen | Set-Content -LiteralPath (Join-Path $run "reopen-$round.json") -Encoding utf8
        $space = Get-Content -Raw -LiteralPath (Join-Path $run "space-$round.json") | ConvertFrom-Json
        $folder = Get-Content -Raw -LiteralPath (Join-Path $run "folder-search-$round.json") | ConvertFrom-Json
        $index = Get-Content -Raw -LiteralPath (Join-Path $run "indexed-$round.json") | ConvertFrom-Json
        if ($index.bytes -ne $space.bytes -or $space.typeBytes -ne $index.typeBytes -or $space.folderItems -ne $index.folderItems) {
            throw 'Space totals differ.'
        }
        foreach ($term in $folder.signatures.PSObject.Properties.Name) {
            if ($folder.signatures.$term -cne $index.signatures.$term) { throw "Search result mismatch for $term." }
        }
    }
    $limited = & $taskDotnet $bench indexed $wire (Join-Path $run 'budget-256') 256
    if ($LASTEXITCODE) { throw '256 MiB replay failed.' }
    $limited | Set-Content -LiteralPath (Join-Path $run 'budget-256.json') -Encoding utf8
    Write-Output $run
} finally { Pop-Location }
