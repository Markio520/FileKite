param([string]$HeadlessOutput)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCache = Join-Path $taskRoot 'artifacts/dependencies/bcu/6.3'
$taskCommit = '2e53fc13be5481029c9f7b7c2597becd1f3394fa'
$taskArchive = Join-Path $taskCache 'source-v6.3.zip'
$taskExpected = '057B5F6727DEC7E5F0CECEE88BC9EC1ABC235A39758679DB041A69B6FEFC3A77'
$taskSource = Join-Path $taskCache "source/Bulk-Crap-Uninstaller-$taskCommit"
New-Item -ItemType Directory -Path $taskCache -Force | Out-Null
if (-not (Test-Path -LiteralPath $taskArchive)) {
    Invoke-WebRequest -UseBasicParsing -Uri "https://codeload.github.com/BCUninstaller/Bulk-Crap-Uninstaller/zip/$taskCommit" -OutFile $taskArchive
}
$taskHash = [System.Security.Cryptography.SHA256]::Create()
$taskStream = [System.IO.File]::OpenRead($taskArchive)
try { $taskActual = [BitConverter]::ToString($taskHash.ComputeHash($taskStream)).Replace('-', '') }
finally { $taskStream.Dispose(); $taskHash.Dispose() }
if ($taskActual -ne $taskExpected) {
    throw 'BCU source archive checksum mismatch. The cached archive has been left untouched for inspection.'
}
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'source/UninstallTools/ApplicationUninstallerEntry.cs'))) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($taskArchive, (Join-Path $taskCache 'source'))
}

if ($HeadlessOutput) {
    $output = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($HeadlessOutput)).FullName
    # These two pinned upstream files mix scanner utilities and WinForms-only overloads.
    # Generate the build-local copies so the downloaded sources remain reusable by other builds.
    function Write-HeadlessSource($relativePath, $methods, $expectedCount) {
        $file = Join-Path $taskSource "source/KlocTools/$relativePath"
        $original = [IO.File]::ReadAllText($file)
        $pattern = "(?ms)^        public static [\w?]+ ($methods)\([^\r\n]*\)\s*\{.*?^        \}\r?\n"
        if ([regex]::Matches($original, $pattern).Count -ne $expectedCount) {
            throw "Review the headless adapter for $relativePath after the upstream source change."
        }
        $adapted = [regex]::Replace($original.Replace('using System.Windows.Forms;', ''), $pattern, '')
        $target = Join-Path $output ([IO.Path]::GetFileName($file))
        if (![IO.File]::Exists($target) -or [IO.File]::ReadAllText($target) -cne $adapted) {
            [IO.File]::WriteAllText($target, $adapted)
        }
    }
    Write-HeadlessSource 'Tools/WindowsTools.cs' 'FlashWindowEx' 1
    Write-HeadlessSource 'Extensions/BooleanTools.cs' 'ToBool|ToCheckState|ToNullBool' 4
}
