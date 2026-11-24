param(
    [string]$Version = '1.0.0',
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Architecture = 'x64',
    [string]$OutputZip = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'initialize-dotnet.ps1')

if (-not $OutputZip) {
    $OutputZip = Join-Path (Split-Path -Parent $repoRoot) "FileKite-v$Version-win-$Architecture.zip"
}

Push-Location $repoRoot
try {
    & $taskDotnet build 'src/FileAssistant.App/FileAssistant.App.csproj' `
        --configuration Release `
        "-p:Platform=$Architecture" `
        "-p:Version=$Version" `
        -p:WindowsPackageType=None `
        -p:SelfContained=true `
        -p:WindowsAppSDKSelfContained=true `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

    $rid = "win-$($Architecture.ToLowerInvariant())"
    $buildOut = Join-Path $repoRoot "src\FileAssistant.App\bin\shared-index\unpackaged\$Architecture\Release\net10.0-windows10.0.26100.0\$rid"
    if (-not (Test-Path -LiteralPath (Join-Path $buildOut 'FileAssistant.App.exe'))) {
        throw "Expected executable not found at $buildOut"
    }

    $stageRoot = Join-Path $repoRoot "artifacts\release-stage"
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
    $pkgDir = Join-Path $stageRoot 'FileKite'
    $appDir = Join-Path $pkgDir 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null

    & robocopy $buildOut $appDir /E /XF *.pdb *.gitkeep /NFL /NDL /NJH /NJS /NC /NS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $pkgDir 'LICENSE.txt')
    $readmeTxt = @"
纸鸢 · FileKite v$Version (Windows $Architecture 免安装版)
======================================================

1. 直接双击本目录下的「FileKite.exe」即可启动软件（无需安装运行库）。
2. 请保持本目录与「app」子文件夹在同一位置，勿单独移出「FileKite.exe」。
3. 所有文件索引、空间扫描、预览、批量整理、格式转换与卸载清理均在本机离线运行。
4. 完整源代码与更新说明：https://github.com/Markio520/FileKite
   开源许可：Apache License 2.0（第三方组件声明见 app\Licenses\）
"@
    [System.IO.File]::WriteAllText((Join-Path $pkgDir 'README.txt'), $readmeTxt, [System.Text.Encoding]::UTF8)

    # Compile lightweight native-icon root launcher FileKite.exe
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $iconPath = Join-Path $repoRoot 'src\FileAssistant.App\Assets\AppIcon.ico'
    $launcherCs = Join-Path $stageRoot 'Launcher.cs'
    $launcherSource = @"
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

[assembly: AssemblyTitle("纸鸢 FileKite")]
[assembly: AssemblyProduct("纸鸢 FileKite")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string target = Path.Combine(baseDir, "app", "FileAssistant.App.exe");
        if (!File.Exists(target))
        {
            MessageBox.Show("未找到主程序 app\\FileAssistant.App.exe，请确保已完整解压整个压缩包。", "纸鸢 · FileKite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('"').Append(args[i].Replace("\"", "\\\"")).Append('"');
        }
        ProcessStartInfo psi = new ProcessStartInfo(target, sb.ToString());
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Path.GetDirectoryName(target);
        Process.Start(psi);
        return 0;
    }
}
"@
    [System.IO.File]::WriteAllText($launcherCs, $launcherSource, [System.Text.Encoding]::UTF8)
    & $csc /nologo /optimize+ /target:winexe "/win32icon:$iconPath" "/out:$(Join-Path $pkgDir 'FileKite.exe')" /reference:System.Windows.Forms.dll $launcherCs
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile root launcher FileKite.exe" }

    if (Test-Path -LiteralPath $OutputZip) { Remove-Item -LiteralPath $OutputZip -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $OutputZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $unpackedMB = [math]::Round(((Get-ChildItem -LiteralPath $pkgDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
    $zipMB = [math]::Round(((Get-Item -LiteralPath $OutputZip).Length / 1MB), 2)
    Write-Output "Package created: $OutputZip"
    Write-Output "Unpacked size: $unpackedMB MB | Zip download size: $zipMB MB"
} finally {
    Pop-Location
}
