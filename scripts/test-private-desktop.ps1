param(
    [Parameter(Mandatory)][string]$Program,
    [Parameter(Mandatory)][string]$ProfileDirectory,
    [string[]]$AppArguments = @(),
    [ValidateRange(5, 900)][int]$TimeoutSeconds = 180,
    [switch]$SmokeApp,
    [switch]$SmokeBackgroundActivation,
    [string]$SilentDropPath
)
$ErrorActionPreference = 'Stop'
$exePath = (Resolve-Path -LiteralPath $Program).Path
$isolatedProfile = [IO.Path]::GetFullPath($ProfileDirectory)
if ($SilentDropPath) {
    if (-not $SmokeBackgroundActivation) { throw 'Silent drop requires the background activation smoke test.' }
    $SilentDropPath = (Resolve-Path -LiteralPath $SilentDropPath).Path
}
$normalProfile = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'FileAssistant')
if ($isolatedProfile.TrimEnd('\').Equals($normalProfile, [StringComparison]::OrdinalIgnoreCase)) { throw 'Select an isolated test profile.' }
[IO.Directory]::CreateDirectory($isolatedProfile) | Out-Null
$oldProfile = $env:FILEASSISTANT_PROFILE_DIRECTORY
$env:FILEASSISTANT_PROFILE_DIRECTORY = $isolatedProfile
try {
    if (-not ('FileAssistantTestDesktop' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading;
public static class FileAssistantTestDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup {
        public int size; public string reserved, desktop, title; public uint x,y,cx,cy,countX,countY,fill,flags;
        public ushort show,reserved2; public IntPtr reservedPtr,stdin,stdout,stderr;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public int id,threadId; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,int flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string file,StringBuilder cmd,IntPtr pa,IntPtr ta,bool handles,uint flags,IntPtr env,string cwd,ref Startup startup,out ProcessInfo pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out int code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,int code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    delegate bool EnumWindowsProc(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop,EnumWindowsProc cb,IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out int id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
    static string Quote(string value) {
        var result = new StringBuilder().Append('"'); int slashes = 0;
        foreach(char c in value) {
            if(c == '\\') { ++slashes; continue; }
            if(c == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes);
            result.Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static string Run(string file,string[] args,int seconds,bool smoke,bool background,string dropPath) {
        var log = new List<string>();
        string name = "FileAssistant-Test-" + Guid.NewGuid().ToString("N");
        IntPtr desk = CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x1FF,IntPtr.Zero);
        if (desk == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        ProcessInfo pi = new ProcessInfo(), activation = new ProcessInfo(), drop = new ProcessInfo();
        double droppedAt = 0;
        try {
            var si = new Startup { size=Marshal.SizeOf(typeof(Startup)), desktop=@"WinSta0\"+name };
            var cmd = new StringBuilder(Quote(file));
            foreach (var arg in args) cmd.Append(" ").Append(Quote(arg));
            if (background) cmd.Append(" --background");
            if(!CreateProcess(file,cmd,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,System.IO.Path.GetDirectoryName(file),ref si,out pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var timer=Stopwatch.StartNew(); bool closed=false; double firstVisible=-1; IntPtr main=IntPtr.Zero;
            while (WaitForSingleObject(pi.process,160)==258) {
                if (timer.Elapsed.TotalSeconds>seconds) throw new TimeoutException("Private desktop process did not complete: "+pi.id);
                if ((!smoke && !background) || closed) continue;
                string caption=null;
                EnumDesktopWindows(desk,(hwnd,data)=>{
                    int id; GetWindowThreadProcessId(hwnd,out id);
                    if(id!=pi.id || !IsWindowVisible(hwnd)) return true;
                    var text=new StringBuilder(256); GetWindowText(hwnd,text,text.Capacity);
                    if(text.Length==0 || !(text.ToString().Contains("FileKite") || text.ToString().Contains("File Assistant") || text.ToString().Contains("文件操作助手") || text.ToString().Contains("檔案操作助手"))) return true;
                    main=hwnd; caption=text.ToString(); return false;
                },IntPtr.Zero);
                if (background && activation.process == IntPtr.Zero) {
                    if (main != IntPtr.Zero) throw new InvalidOperationException("Background startup displayed a window.");
                    if (timer.Elapsed.TotalSeconds < 4.0) continue;
                    if (!string.IsNullOrEmpty(dropPath)) {
                        if (drop.process == IntPtr.Zero) {
                            var siDrop = new Startup { size=Marshal.SizeOf(typeof(Startup)), desktop=si.desktop };
                            var dropArgs = new StringBuilder(Quote(file)).Append(" ").Append(Quote(dropPath));
                            if (!CreateProcess(file,dropArgs,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,System.IO.Path.GetDirectoryName(file),ref siDrop,out drop))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                            droppedAt=timer.Elapsed.TotalSeconds;
                            continue;
                        }
                        if (timer.Elapsed.TotalSeconds-droppedAt<2.0 || WaitForSingleObject(drop.process,0)!=0) continue;
                        int dropCode;
                        if (!GetExitCodeProcess(drop.process,out dropCode) || dropCode!=0) throw new InvalidOperationException("Silent external import failed.");
                        log.Add("An additional process submitted the fixture without showing the background window.");
                    }
                    log.Add("Background startup kept the main window hidden for at least four seconds.");
                    var second = new Startup { size=Marshal.SizeOf(typeof(Startup)), desktop=si.desktop };
                    if (!CreateProcess(file,new StringBuilder(Quote(file)),IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,System.IO.Path.GetDirectoryName(file),ref second,out activation))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    log.Add("Started the same executable again to activate its existing window.");
                    continue;
                }
                if(main!=IntPtr.Zero && firstVisible<0) { firstVisible=timer.Elapsed.TotalSeconds; log.Add("Native main window on isolated desktop: "+caption); }
                if(firstVisible>=0 && timer.Elapsed.TotalSeconds-firstVisible>=1.5) {
                    closed=PostMessage(main,0x0010,IntPtr.Zero,IntPtr.Zero);
                    log.Add("Requested a normal window close.");
                }
            }
            int code; GetExitCodeProcess(pi.process,out code);
            if (background && activation.process != IntPtr.Zero) {
                int activated;
                if (WaitForSingleObject(activation.process,5000)!=0 || !GetExitCodeProcess(activation.process,out activated) || activated!=0)
                    throw new InvalidOperationException("The activation process did not exit successfully.");
            }
            if(code!=0 || ((smoke || background) && !closed)) throw new InvalidOperationException("Private desktop app exited "+code+", native window closed="+closed);
            log.Add("Private desktop process completed, exit 0 ("+timer.Elapsed.TotalSeconds.ToString("F2")+" s).");
            return string.Join(Environment.NewLine, log);
        }
        finally {
            if(pi.process!=IntPtr.Zero && WaitForSingleObject(pi.process,0)==258) { TerminateProcess(pi.process,2); WaitForSingleObject(pi.process,2500); }
            if(drop.process!=IntPtr.Zero && WaitForSingleObject(drop.process,0)==258) { TerminateProcess(drop.process,2); WaitForSingleObject(drop.process,2500); }
            if(drop.thread!=IntPtr.Zero) CloseHandle(drop.thread);
            if(drop.process!=IntPtr.Zero) CloseHandle(drop.process);
            if(activation.process!=IntPtr.Zero && WaitForSingleObject(activation.process,0)==258) { TerminateProcess(activation.process,2); WaitForSingleObject(activation.process,2500); }
            if(activation.thread!=IntPtr.Zero) CloseHandle(activation.thread);
            if(activation.process!=IntPtr.Zero) CloseHandle(activation.process);
            if(pi.thread!=IntPtr.Zero) CloseHandle(pi.thread);
            if(pi.process!=IntPtr.Zero) CloseHandle(pi.process);
            CloseDesktop(desk);
        }
    }
}
'@
    }
    [FileAssistantTestDesktop]::Run($exePath,$AppArguments,$TimeoutSeconds,$SmokeApp.IsPresent,$SmokeBackgroundActivation.IsPresent,$SilentDropPath)
} finally {
    $env:FILEASSISTANT_PROFILE_DIRECTORY = $oldProfile
}
