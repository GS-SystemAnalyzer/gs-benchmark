#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $Config,
    [string] $Output,
    [string[]] $ScenarioMethods = @(),
    [switch] $ValidateOnly,
    [switch] $LibraryOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:Methods = @('Idle', 'AppEmpty', 'AppHit', 'ChangedRescan', 'PersistedRestore',
    'ConcurrentRoots', 'ActiveCancelRestart', 'QueuedCancelRestart', 'HistoryRanges', 'Reconnect')
$script:Failures = New-Object 'System.Collections.Generic.List[string]'

function Assert-Condition($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-FullPath([string] $Path) {
    Assert-Condition ([IO.Path]::IsPathRooted($Path)) 'An absolute path is required.'
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -eq [IO.Path]::GetPathRoot($full)) { return $full }
    return $full.TrimEnd('\', '/')
}

function Test-Within([string] $Parent, [string] $Child) {
    $parentPath = Get-FullPath $Parent
    $childPath = Get-FullPath $Child
    return $childPath.Equals($parentPath, [StringComparison]::OrdinalIgnoreCase) -or
        $childPath.StartsWith($parentPath.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoLink([string] $Path) {
    $current = Get-FullPath $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            Assert-Condition (((Get-Item -Force -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked ownership path.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-Hash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TextHash([string] $Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($script:Utf8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Read-Json([string] $Path) {
    Assert-NoLink $Path
    Assert-Condition ((Get-Item -LiteralPath $Path).Length -le 16777216) 'Oversized control document.'
    return [IO.File]::ReadAllText($Path, $script:Utf8) | ConvertFrom-Json
}

function Write-NewJson([string] $Path, $Value) {
    Assert-NoLink $Path
    $partial = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    $bytes = $script:Utf8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 30 -Compress) + "`n")
    $file = [IO.File]::Open($partial, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
    [IO.File]::Move($partial, $Path)
}

function Write-Ledger([string] $Stage, $Fields) {
    $record = [ordered]@{ schemaVersion = 1; runId = $script:RunId; stage = $Stage
        utc = [DateTimeOffset]::UtcNow.ToString('o'); ticks = [Diagnostics.Stopwatch]::GetTimestamp().ToString()
        frequencyHz = [Diagnostics.Stopwatch]::Frequency.ToString(); fields = $Fields }
    $script:Ledger.WriteLine(($record | ConvertTo-Json -Depth 30 -Compress))
    $script:Ledger.Flush()
    $script:Ledger.BaseStream.Flush($true)
}

function Add-Failure([string] $Message) {
    if ($script:Failures.Count -lt 64) { $script:Failures.Add($Message) }
}

function Write-CleanupLedger([string] $Stage, $Fields) {
    try { Write-Ledger $Stage $Fields }
    catch { Add-Failure ('Cleanup ledger failed: ' + $_.Exception.GetType().Name) }
}

function Get-Inventory([string] $Root, [string[]] $ExcludeDirectories = @()) {
    Assert-NoLink $Root
    $rootPath = Get-FullPath $Root
    $excluded = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $ExcludeDirectories) {
        $full = Get-FullPath $path
        Assert-Condition ((Test-Within $rootPath $full) -and $full -ine $rootPath) 'Invalid inventory exclusion.'
        [void]$excluded.Add($full)
    }
    $items = New-Object 'System.Collections.Generic.List[object]'
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue($rootPath)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -Force -LiteralPath $pending.Dequeue()) {
            Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked inventory member.'
            if ($item.PSIsContainer) {
                if (-not $excluded.Contains($item.FullName)) { $pending.Enqueue($item.FullName) }
                continue
            }
            Assert-Condition ($items.Count -lt 100000 -and $item.Length -le 1073741824) 'Inventory limit exceeded.'
            $items.Add([ordered]@{ path = $item.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
                bytes = $item.Length; sha256 = Get-Hash $item.FullName })
        }
    }
    return @($items | Sort-Object { $_.path } -CaseSensitive)
}

function Get-InventoryHash($Inventory) {
    $lines = @($Inventory | ForEach-Object { $_.path + "`t" + $_.bytes + "`t" + $_.sha256 })
    return Get-TextHash (($lines -join "`n") + "`n")
}

function Assert-Artifact($Spec) {
    $rootPath = Get-FullPath $Spec.root
    Assert-Condition ($Spec.sha256 -cmatch '^[a-f0-9]{64}$') 'Invalid artifact-set hash.'
    $inventory = @(Get-Inventory $rootPath)
    Assert-Condition ($inventory.Count -gt 0 -and (Get-InventoryHash $inventory) -ceq $Spec.sha256) 'Artifact set changed.'
    $exe = Get-FullPath (Join-Path $rootPath $Spec.executable)
    Assert-Condition ((Test-Within $rootPath $exe) -and $exe.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $exe -PathType Leaf)) 'Invalid supplied executable.'
    return $inventory
}

function Read-Git([string] $Root, [string[]] $Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command git -CommandType Application).Source
    $start.WorkingDirectory = $Root
    $start.Arguments = (@($Arguments | ForEach-Object { [GsScenarioProcess]::Quote($_) }) -join ' ')
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $script:Utf8; $start.StandardErrorEncoding = $script:Utf8
    $start.EnvironmentVariables.Clear()
    foreach ($key in @('SystemRoot', 'WINDIR', 'TEMP', 'TMP')) { $start.EnvironmentVariables[$key] = [Environment]::GetEnvironmentVariable($key) }
    $start.EnvironmentVariables['GIT_CONFIG_NOSYSTEM'] = '1'; $start.EnvironmentVariables['GIT_CONFIG_GLOBAL'] = 'NUL'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            Assert-Condition ($process.WaitForExit(5000)) 'Source inspection did not settle after kill.'
            throw 'Source inspection timed out.'
        }
        Assert-Condition ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) 'Source output drain timed out.'
        $text = $stdout.GetAwaiter().GetResult(); [void]$stderr.GetAwaiter().GetResult()
        Assert-Condition ($process.ExitCode -eq 0) 'Source inspection failed.'
        return $text
    } finally { $process.Dispose() }
}

function Assert-Source($Spec) {
    $rootPath = Get-FullPath $Spec.root
    Assert-NoLink $rootPath
    $sha = (Read-Git $rootPath @('rev-parse', 'HEAD')).Trim()
    $dirty = (Read-Git $rootPath @('status', '--porcelain', '-z')).Length -gt 0
    Assert-Condition ($sha -ceq $Spec.sha -and $dirty -eq $Spec.dirty) 'Source checkout identity changed.'
    $names = (Read-Git $rootPath @('ls-files', '-z', '--cached', '--others', '--exclude-standard')).Split([char]0) |
        Where-Object { $_.Length -gt 0 } | Sort-Object -Unique -CaseSensitive
    $inventory = @($names | ForEach-Object {
        Assert-Condition (-not $_.Contains("`n") -and -not $_.Contains("`t")) 'Unsupported source filename.'
        $path = Get-FullPath (Join-Path $rootPath $_)
        Assert-Condition (Test-Within $rootPath $path) 'Source member escapes checkout.'
        Assert-NoLink $path
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{ path = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = Get-Hash $path }
        } else {
            Assert-Condition (-not (Test-Path -LiteralPath $path)) 'Source submodules require an explicit flattened inventory.'
            [ordered]@{ path = $_; bytes = -1; sha256 = 'missing' }
        }
    })
    Assert-Condition ((Get-InventoryHash $inventory) -ceq $Spec.sourceSha256) 'Source content hash changed.'
    return $inventory
}

function Get-SentinelSnapshot($Paths) {
    return @($Paths | ForEach-Object {
        $path = Get-FullPath $_
        try {
            Assert-NoLink $path
            if (-not (Test-Path -LiteralPath $path)) { [ordered]@{ path = $path; outcome = 'absent' }; return }
            $item = Get-Item -Force -LiteralPath $path
            $entries = @(if ($item.PSIsContainer) { Get-Inventory $path } else {
                @([ordered]@{ path = $item.Name; bytes = $item.Length; sha256 = Get-Hash $path })
            })
            $times = @($entries | ForEach-Object {
                $file = if ($item.PSIsContainer) { Join-Path $path $_.path } else { $path }
                [ordered]@{ path = $_.path; lastWriteUtc = (Get-Item -LiteralPath $file).LastWriteTimeUtc.ToString('o') }
            })
            $dirs = @(if ($item.PSIsContainer) {
                @(Get-ChildItem -Force -Directory -Recurse -LiteralPath $path | ForEach-Object {
                    [ordered]@{ path = $_.FullName.Substring($path.Length + 1); lastWriteUtc = $_.LastWriteTimeUtc.ToString('o') }
                } | Sort-Object { $_.path })
            })
            [ordered]@{ path = $path; outcome = 'observed'; entries = $entries; times = $times; directories = $dirs
                lastWriteUtc = $item.LastWriteTimeUtc.ToString('o') }
        } catch { [ordered]@{ path = $path; outcome = 'unknown'; errorType = $_.Exception.GetType().Name } }
    })
}

function Assert-SentinelsUnchanged($Before, $After) {
    Assert-Condition (@($Before | Where-Object outcome -eq 'unknown').Count -eq 0 -and
        @($After | Where-Object outcome -eq 'unknown').Count -eq 0) 'Unknown external sentinel state.'
    $first = ConvertTo-Json -InputObject @($Before) -Depth 30 -Compress
    $last = ConvertTo-Json -InputObject @($After) -Depth 30 -Compress
    Assert-Condition ($first -ceq $last) 'External sentinel changed.'
}

function Assert-OutputLocation([string] $RunRoot, [string] $StateRoot, [string[]] $Inputs) {
    foreach ($path in @($StateRoot) + $Inputs) {
        Assert-Condition (-not (Test-Within $path $RunRoot) -and -not (Test-Within $RunRoot $path)) 'Output overlaps an input or product state.'
    }
    Assert-Condition ([IO.Path]::GetPathRoot($RunRoot) -ieq [IO.Path]::GetPathRoot($StateRoot)) 'State archive requires same-volume output.'
}

function Assert-ControlAction([string] $Method, [string] $Action, [int] $Handled, [double] $ElapsedSeconds) {
    Assert-Condition ($ElapsedSeconds -lt 240 -and $Handled -eq 0) 'Control limit or deadline exceeded.'
    Assert-Condition (($Action -ceq 'restart' -and $Method -cin @('PersistedRestore', 'Reconnect')) -or
        ($Action -ceq 'mutate' -and $Method -ceq 'ChangedRescan')) 'Unexpected scenario control.'
}

function Assert-Environment($Spec) {
    Assert-Condition ([Environment]::OSVersion.Platform -eq 'Win32NT' -and [Environment]::Is64BitProcess) 'Windows X64 launcher required.'
    $actual = [ordered]@{ sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        profile = [Environment]::GetFolderPath('UserProfile'); roaming = [Environment]::GetFolderPath('ApplicationData')
        local = [Environment]::GetFolderPath('LocalApplicationData'); temp = Get-FullPath ([IO.Path]::GetTempPath()) }
    Assert-Condition ($Spec.disposable -eq $true -and $Spec.id.Length -gt 0 -and $actual.sid -ceq $Spec.sid) 'Disposable token identity mismatch.'
    foreach ($key in @('profile', 'roaming', 'local', 'temp')) {
        Assert-NoLink $actual[$key]
        Assert-Condition ((Get-FullPath $Spec.$key) -ieq (Get-FullPath $actual[$key])) ('Resolved known-folder mismatch: ' + $key)
    }
    return $actual
}

function Get-Listeners {
    return @([GsScenarioProcess]::Listeners() | Where-Object LocalPort -eq 5200)
}

function Assert-Port([Nullable[int]] $OwnedPid = $null) {
    $listeners = @(Get-Listeners)
    foreach ($listener in $listeners) {
        Assert-Condition ($null -ne $OwnedPid -and $listener.OwningProcess -eq $OwnedPid) 'Port 5200 has a foreign listener.'
    }
    if ($null -ne $OwnedPid) { Assert-Condition ($listeners.Count -gt 0) 'Owned backend listener is unavailable.' }
}

function Initialize-NativeLauncher {
    if ('GsScenarioProcess' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

public sealed class GsScenarioProcess : IDisposable {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup {
        public int cb; public string reserved, desktop, title;
        public int x,y,xSize,ySize,xChars,yChars,fill,flags; public short show,reserved2;
        public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct Info { public IntPtr process,thread; public int pid,tid; }
    [StructLayout(LayoutKind.Sequential)] struct Security { public int length; public IntPtr descriptor; public int inherit; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit {
        public long processTime,jobTime; public uint flags; public UIntPtr min,max; public uint active;
        public UIntPtr affinity; public uint priority,scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit {
        public BasicLimit basic; public ulong readOps,writeOps,otherOps,readBytes,writeBytes,otherBytes;
        public UIntPtr processMemory,jobMemory,peakProcess,peakJob;
    }
    [StructLayout(LayoutKind.Sequential)] struct Accounting {
        public long user,kernel,periodUser,periodKernel; public uint faults,total,active,terminated;
    }
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcessW(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref Startup startup, out Info info);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateFileW(string name, uint access, uint share, ref Security security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32", SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32", SetLastError=true)] static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
    [DllImport("kernel32", SetLastError=true)] static extern uint GetConsoleProcessList([Out] uint[] ids, uint size);
    [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, uint family, int kind, uint reserved);
    [DllImport("kernel32", SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimit value, int size);
    [DllImport("kernel32", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job, int type, out Accounting value, int size, IntPtr length);
    [DllImport("kernel32", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32", SetLastError=true)] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("advapi32", SetLastError=true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    IntPtr process,thread,job; bool resumed,assigned;
    public int Pid { get; private set; }
    public long CreatedFileTime { get; private set; }
    public string Image { get; private set; }
    public string Sid { get; private set; }
    static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public static bool HasConsole() { return GetConsoleProcessList(new uint[1], 1) != 0; }
    public static uint[] ConsoleMembers() {
        uint[] ids=new uint[16];
        for(int attempt=0;attempt<4;attempt++) {
            uint count=GetConsoleProcessList(ids,(uint)ids.Length); Check(count!=0);
            if(count<=ids.Length) { Array.Resize(ref ids,(int)count); return ids; }
            if(count>65536) throw new InvalidOperationException("Console membership limit exceeded");
            ids=new uint[count];
        }
        throw new InvalidOperationException("Console membership did not stabilize");
    }
    public sealed class Listener {
        public uint Family, State, OwningProcess;
        public int LocalPort;
        public string LocalAddress;
    }
    public static Listener[] Listeners() {
        var rows=new List<Listener>();
        foreach(uint family in new uint[]{2,23}) {
            int size=0; uint code=GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,3,0);
            if(code!=122) throw new Win32Exception((int)code);
            bool read=false;
            for(int attempt=0;attempt<4;attempt++) {
                if(size<4 || size>16777216) throw new InvalidOperationException("Invalid listener table size");
                int allocated=size; IntPtr table=Marshal.AllocHGlobal(size);
                try {
                    code=GetExtendedTcpTable(table,ref size,false,family,3,0);
                    if(code==122) continue;
                    if(code!=0) throw new Win32Exception((int)code);
                    int count=Marshal.ReadInt32(table), stride=family==2?24:56;
                    if(count<0 || 4L+(long)count*stride>allocated) throw new InvalidOperationException("Invalid listener table count");
                    for(int i=0;i<count;i++) {
                        IntPtr row=IntPtr.Add(table,4+i*stride); int port=family==2?8:20;
                        byte[] address=new byte[family==2?4:16]; Marshal.Copy(IntPtr.Add(row,family==2?4:0),address,0,address.Length);
                        // OWNER_PID_LISTENER selects listeners in the OS; retain the raw state for diagnostics.
                        rows.Add(new Listener { Family=family, State=unchecked((uint)Marshal.ReadInt32(row,family==2?0:48)),
                            OwningProcess=unchecked((uint)Marshal.ReadInt32(row,family==2?20:52)),
                            LocalPort=Marshal.ReadByte(row,port)*256+Marshal.ReadByte(row,port+1), LocalAddress=new IPAddress(address).ToString() });
                    }
                    read=true; break;
                } finally { Marshal.FreeHGlobal(table); }
            }
            if(!read) throw new InvalidOperationException("Listener table did not stabilize");
        }
        return rows.ToArray();
    }
    public static string Quote(string value) {
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("NUL argument");
        var b = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) {
            if (c == '\\') { slashes++; continue; }
            b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); b.Append(c); slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }
    public GsScenarioProcess(string exe, string[] args, string cwd, string environment, string stdout, string stderr) {
        if (!HasConsole()) throw new InvalidOperationException("A shared launcher console is required");
        var security = new Security { length = Marshal.SizeOf(typeof(Security)), inherit = 1 };
        IntPtr output=IntPtr.Zero,error=IntPtr.Zero,input=IntPtr.Zero,env=IntPtr.Zero;
        try {
            output=CreateFileW(stdout,0x40000000,1,ref security,1,0x80,IntPtr.Zero); Check(output.ToInt64()!=-1);
            error=CreateFileW(stderr,0x40000000,1,ref security,1,0x80,IntPtr.Zero); Check(error.ToInt64()!=-1);
            input=CreateFileW("NUL",0x80000000,3,ref security,3,0x80,IntPtr.Zero); Check(input.ToInt64()!=-1);
            var startup=new Startup { cb=Marshal.SizeOf(typeof(Startup)),flags=0x100,input=input,output=output,error=error };
            var command=new StringBuilder(Quote(exe)); foreach(string arg in args) command.Append(' ').Append(Quote(arg));
            env=Marshal.StringToHGlobalUni(environment); Info info;
            Check(CreateProcessW(exe,command,IntPtr.Zero,IntPtr.Zero,true,0x4|0x200|0x400,env,cwd,ref startup,out info));
            process=info.process; thread=info.thread; Pid=info.pid;
            long created,exited,kernel,user; Check(GetProcessTimes(process,out created,out exited,out kernel,out user)); CreatedFileTime=created;
            uint size=32768; var path=new StringBuilder((int)size); Check(QueryFullProcessImageNameW(process,0,path,ref size)); Image=path.ToString();
            IntPtr token; Check(OpenProcessToken(process,8,out token));
            try { using(var identity=new WindowsIdentity(token)) Sid=identity.User.Value; } finally { CloseHandle(token); }
        } catch(Exception original) {
            try { Dispose(); } catch(Exception cleanup) { throw new AggregateException(original,cleanup); }
            throw;
        }
        finally {
            foreach(var h in new[]{output,error,input}) if(h!=IntPtr.Zero && h.ToInt64()!=-1) CloseHandle(h);
            if(env!=IntPtr.Zero) Marshal.FreeHGlobal(env);
        }
    }
    public void Start() {
        if(resumed) throw new InvalidOperationException("Already started");
        job=CreateJobObjectW(IntPtr.Zero,null); Check(job!=IntPtr.Zero);
        var limit=new ExtendedLimit { basic=new BasicLimit { flags=0x2000 } };
        Check(SetInformationJobObject(job,9,ref limit,Marshal.SizeOf(typeof(ExtendedLimit))));
        Check(AssignProcessToJobObject(job,process)); assigned=true;
        Check(ResumeThread(thread)!=0xffffffff); resumed=true; CloseHandle(thread); thread=IntPtr.Zero;
    }
    public bool Wait(int milliseconds) {
        uint result=WaitForSingleObject(process,(uint)milliseconds);
        if(result==0xffffffff) Check(false); return result==0;
    }
    public uint ActiveProcesses {
        get { if(!assigned) return Wait(0)?0u:1u; Accounting value;
            Check(QueryInformationJobObject(job,1,out value,Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero)); return value.active; }
    }
    public int ExitCode { get { uint value; Check(Wait(0)); Check(GetExitCodeProcess(process,out value)); return unchecked((int)value); } }
    public void Break() { if(!Wait(0)) Check(GenerateConsoleCtrlEvent(1,(uint)Pid)); }
    public void Force() { if(assigned) Check(TerminateJobObject(job,1)); else if(!Wait(0)) Check(TerminateProcess(process,1)); }
    public void Dispose() {
        Exception failure=null;
        try {
            if(process!=IntPtr.Zero && (!Wait(0) || ActiveProcesses!=0)) {
                Force();
                if(!Wait(30000)) throw new TimeoutException("Owned primary did not settle");
                var clock=System.Diagnostics.Stopwatch.StartNew();
                while(ActiveProcesses!=0 && clock.ElapsedMilliseconds<30000) System.Threading.Thread.Sleep(20);
                if(ActiveProcesses!=0) throw new TimeoutException("Owned job did not settle");
            }
        } catch(Exception error) { failure=error; }
        finally {
            foreach(var handle in new[]{job,thread,process}) {
                if(handle==IntPtr.Zero) continue;
                try { Check(CloseHandle(handle)); } catch(Exception error) { failure=failure==null?error:new AggregateException(failure,error); }
            }
            job=thread=process=IntPtr.Zero;
        }
        if(failure!=null) throw failure;
    }
}
'@
}

function New-ChildEnvironment([string] $TraceRoot, [string] $DriverConfig, [bool] $TraceEnabled = $true) {
    $environment = @{}
    foreach ($key in @('SystemRoot', 'WINDIR', 'COMSPEC', 'SystemDrive', 'USERPROFILE', 'APPDATA', 'LOCALAPPDATA',
        'TEMP', 'TMP', 'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramData')) {
        $value = [Environment]::GetEnvironmentVariable($key)
        if ($null -ne $value) { $environment[$key] = $value }
    }
    $environment['PATH'] = [Environment]::SystemDirectory + ';' + [Environment]::GetEnvironmentVariable('SystemRoot')
    if ($TraceEnabled) {
        $environment['GS_BENCHMARK_RUN_ID'] = $script:RunId
        $environment['GS_BENCHMARK_OUTPUT'] = $TraceRoot
    }
    if ($DriverConfig) { $environment['GS_BENCHMARK_SCENARIO'] = $DriverConfig }
    return (($environment.Keys | Sort-Object | ForEach-Object { $_ + '=' + $environment[$_] }) -join "`0") + "`0`0"
}

function Get-LaunchArguments([string] $Role) {
    [string[]]$arguments = @()
    if ($Role -ceq 'backend') { $arguments = @('--urls', 'http://localhost:5200') }
    else { Assert-Condition ($Role -ceq 'client') 'Unknown launch role.' }
    return ,$arguments
}

function New-Launch([string] $Role, [string] $Id, $Spec, [string] $DriverConfig = '', [bool] $TraceEnabled = $true) {
    $directory = Join-Path $script:RunRoot ('launches/' + $Id)
    [void][IO.Directory]::CreateDirectory($directory)
    Write-NewJson (Join-Path $directory 'owner.json') @{ schemaVersion = 1; runId = $script:RunId; detail = $script:Detail }
    $exe = Get-FullPath (Join-Path $Spec.root $Spec.executable)
    $arguments = Get-LaunchArguments $Role
    $entry = @{ id = $Id; role = $Role; directory = $directory; process = $null; outcome = 'failed'; exitCode = $null; settled = $false }
    $script:Launches.Add($entry)
    Write-Ledger 'launch.requested' @{ id = $Id; role = $Role; executable = $exe; artifactSha256 = $Spec.sha256; arguments = $arguments }
    try {
        [void](Assert-Artifact $Spec)
        $child = New-Object GsScenarioProcess($exe, [string[]]$arguments, (Get-FullPath $Spec.root),
            (New-ChildEnvironment $directory $DriverConfig $TraceEnabled), (Join-Path $directory 'stdout.log'), (Join-Path $directory 'stderr.log'))
        $entry.process = $child
        Write-Ledger 'launch.created' @{ id = $Id; processId = $child.Pid; createdFileTime = $child.CreatedFileTime.ToString(); image = $child.Image; sid = $child.Sid }
        Assert-Condition ($child.Sid -ceq $script:Verified.sid -and $child.Image -ieq $exe) 'Created process identity mismatch.'
        $child.Start()
        Write-Ledger 'launch.started' @{ id = $Id; processId = $child.Pid }
        return $entry
    } catch {
        Write-CleanupLedger 'launch.failed' @{ id = $Id; errorType = $_.Exception.GetType().Name }
        throw
    }
}

function Wait-Backend($Entry) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 30) {
        Assert-Condition (-not $Entry.process.Wait(0)) 'Backend exited before readiness.'
        foreach ($listener in Get-Listeners) {
            Assert-Condition ($listener.OwningProcess -eq $Entry.process.Pid) 'Foreign listener appeared during readiness.'
        }
        try {
            $request = [Net.HttpWebRequest]::Create('http://localhost:5200/health')
            $request.Proxy = $null; $request.Timeout = 500; $request.ReadWriteTimeout = 500
            $response = $request.GetResponse()
            try { $ready = [int]$response.StatusCode -eq 200 } finally { $response.Dispose() }
            if ($ready) {
                Assert-Port $Entry.process.Pid
                Write-Ledger 'launch.ready' @{ id = $Entry.id; processId = $Entry.process.Pid }
                return
            }
        } catch [Net.WebException] { if ($_.Exception.Response) { $_.Exception.Response.Dispose() } }
        Start-Sleep -Milliseconds 100
    }
    throw 'Backend readiness timed out.'
}

function Get-ExceptionSummary([Exception] $Exception) {
    $pending = New-Object 'System.Collections.Generic.Queue[Exception]'
    $pending.Enqueue($Exception)
    $count = 0
    while ($pending.Count -gt 0 -and $count -lt 16) {
        $current = $pending.Dequeue(); $count++
        @{ type = $current.GetType().FullName; nativeErrorCode = $(if ($current -is [ComponentModel.Win32Exception]) { $current.NativeErrorCode } else { $null }) }
        if ($current -is [AggregateException]) {
            foreach ($inner in $current.InnerExceptions) { if ($pending.Count -lt 16) { $pending.Enqueue($inner) } }
        } elseif ($null -ne $current.InnerException) { $pending.Enqueue($current.InnerException) }
    }
}

function Stop-Launch($Entry, [bool] $Graceful) {
    if ($null -eq $Entry.process) { return }
    if ($Entry.ContainsKey('settled') -and $Entry.settled) { return }
    $child = $Entry.process
    if (-not $child.Wait(0) -and $Graceful) {
        Write-CleanupLedger 'launch.stop_requested' @{ id = $Entry.id; mechanism = 'CTRL_BREAK_EVENT' }
        try {
            if ($child.GetType().FullName -eq 'GsScenarioProcess') {
                Write-CleanupLedger 'launch.console_observed' @{ id = $Entry.id; caller = $PID; members = [GsScenarioProcess]::ConsoleMembers() }
            }
            $child.Break()
        } catch { Write-CleanupLedger 'launch.signal_failed' @{ id = $Entry.id; errorType = $_.Exception.GetType().Name; errors = @(Get-ExceptionSummary $_.Exception) } }
        [void]$child.Wait(30000)
    }
    if (-not $child.Wait(0) -or $child.ActiveProcesses -ne 0) {
        $Entry.outcome = 'forced'
        Write-CleanupLedger 'launch.force_requested' @{ id = $Entry.id }
        $child.Force()
        Assert-Condition ($child.Wait(30000)) 'Owned process did not exit after forced stop.'
    } else { $Entry.outcome = if ($child.ExitCode -eq 0) { 'exited' } else { 'failed' } }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($child.ActiveProcesses -ne 0 -and $clock.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 20 }
    Assert-Condition ($child.ActiveProcesses -eq 0) 'Owned job still contains processes.'
    $Entry.exitCode = $child.ExitCode
    $Entry.settled = $true
    Write-CleanupLedger 'launch.exited' @{ id = $Entry.id; outcome = $Entry.outcome; exitCode = $Entry.exitCode; activeProcesses = $child.ActiveProcesses }
}

function Close-OwnedLaunches($Entries) {
    foreach ($entry in $Entries) {
        if ($null -eq $entry.process) { continue }
        try { Stop-Launch $entry ($entry.role -ceq 'backend') }
        catch {
            Add-Failure ('Owned shutdown failed: ' + $_.Exception.GetType().Name)
            Write-CleanupLedger 'launch.unsettled' @{ id = $entry.id; errorType = $_.Exception.GetType().Name }
        } finally {
            try { $entry.process.Dispose() }
            catch { Add-Failure ('Owned disposal failed: ' + $_.Exception.GetType().Name) }
        }
    }
}

function New-Fixture([string] $Root, [bool] $Large) {
    Assert-Condition (-not (Test-Path -LiteralPath $Root)) 'Fixture target already exists.'
    $directories = New-Object 'System.Collections.Generic.List[string]'
    $files = New-Object 'System.Collections.Generic.List[object]'
    $directories.Add('')
    if ($Large) {
        for ($r = 0; $r -lt 5; $r++) {
            $directories.Add("Root_$r")
            for ($s = 0; $s -lt 100; $s++) {
                $directory = "Root_$r/Sub_$s"; $directories.Add($directory)
                for ($f = 0; $f -lt 100; $f++) { $files.Add(@{ path = "$directory/file_$f.txt"; bytes = 0; ageDays = 2 }) }
            }
        }
    } else {
        foreach ($directory in @('A', 'A/deep', 'B')) { $directories.Add($directory) }
        foreach ($file in @(@('zero.txt', 0, 2), @('root.txt', 3, 2), @('A/a.txt', 5, 15), @('A/a.bin', 11, 15),
            @('A/deep/deep.bin', 17, 120), @('B/plain', 23, 730), @('B/old.log', 31, 730))) {
            $files.Add(@{ path = $file[0]; bytes = $file[1]; ageDays = $file[2] })
        }
    }
    $origin = [DateTime]::UtcNow
    foreach ($directory in $directories) { [void][IO.Directory]::CreateDirectory((Join-Path $Root $directory)) }
    foreach ($file in $files) {
        $path = Join-Path $Root $file.path
        [IO.File]::WriteAllBytes($path, (New-Object byte[] $file.bytes))
        [IO.File]::SetLastWriteTimeUtc($path, $origin.AddDays(-$file.ageDays))
    }
    $inventory = @(Get-Inventory $Root)
    $observedDirectories = @('') + @(Get-ChildItem -Directory -Recurse -LiteralPath $Root | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1).Replace('\', '/') })
    Assert-Condition ($inventory.Count -eq $files.Count -and $observedDirectories.Count -eq $directories.Count) 'Fixture membership mismatch.'
    $total = ($inventory | ForEach-Object { $_.bytes } | Measure-Object -Sum).Sum
    Assert-Condition ($total -eq $(if ($Large) { 0 } else { 90 })) 'Fixture byte mismatch.'
    return @{ id = $(if ($Large) { 'scanner-50k-v1' } else { 'tiny-v1' }); root = $Root
        directories = $directories.ToArray(); observedDirectories = $observedDirectories
        files = $files.ToArray(); originUtc = $origin.ToString('o'); inventory = $inventory }
}

function Set-ScanRootKeys($Attempt, $Launches) {
    Assert-Condition (@($Attempt.scans).Count -eq @($Attempt.scan_ids).Count) 'Scan binding membership mismatch.'
    foreach ($scan in $Attempt.scans) {
        Assert-Condition ($scan.fixture_artifact -ceq ('cases/' + $Attempt.id + '/fixtures.json') -and
            $scan.fixture_name -cin @('primary', 'secondary', 'large')) 'Invalid scan fixture reference.'
        $launch = @($Launches | Where-Object { $_.id -ceq $scan.backend_launch -and $_.role -ceq 'backend' })
        Assert-Condition ($launch.Count -eq 1 -and $Attempt.backend_launches -ccontains $scan.backend_launch) 'Scan backend launch missing.'
        $fixtures = Read-Json (Join-Path $script:RunRoot $scan.fixture_artifact)
        $root = Get-FullPath $fixtures.($scan.fixture_name).root
        $key = Get-TextHash ($launch[0].epoch + "`n" + $root.Replace('\', '/').TrimEnd('/').ToUpperInvariant())
        Assert-Condition ($scan.root_key -ceq '' -or $scan.root_key -ceq $key) 'Conflicting scan root key.'
        $scan.root_key = $key
    }
}

function Complete-Capture($Spec, [string] $Started, $Attempts, [string[]] $FixtureRoots) {
    $normalized = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $script:Launches) {
        try {
            $traces = @(Get-ChildItem -LiteralPath $entry.directory -Filter '*.jsonl*')
            Assert-Condition ($null -ne $entry.process -and $traces.Count -eq 1) 'Launch lacks process/trace identity.'
            $reader = [IO.File]::OpenText($traces[0].FullName)
            try { $first = $reader.ReadLine() | ConvertFrom-Json } finally { $reader.Dispose() }
            $epoch = if ($entry.role -ceq 'backend') { $first.benchmarkTrace.producerEpoch } else { $first.producerEpoch }
            Assert-Condition ($epoch -cmatch '^[a-f0-9]{32}$' -and $first.processId -eq $entry.process.Pid) 'Trace process identity mismatch.'
            $normalized.Add(@{ id = $entry.id; role = $entry.role; process_id = $entry.process.Pid; epoch = $epoch
                artifact_sha256 = $Spec.($entry.role).sha256; trace = $traces[0].FullName.Substring($script:RunRoot.Length + 1).Replace('\', '/')
                outcome = $entry.outcome; exit_code = $entry.exitCode })
        } catch { Add-Failure ('Trace identity unavailable: ' + $_.Exception.GetType().Name) }
    }
    $environment = @{ backend_sha256 = $Spec.backend.sha256; client_sha256 = $Spec.client.sha256
        core_source_sha256 = $Spec.core.sourceSha256; harness_source_sha256 = $Spec.harness.sourceSha256
        source_identity_basis = 'verified-checkout-and-artifact-sets;build-link-supplied'; sdk = $Spec.versions.sdk; flutter = $Spec.versions.flutter; dart = $Spec.versions.dart
        os = 'Windows'; architecture = 'X64'; tfm = 'net10.0-windows'; environment_id = $Spec.environment.id }
    $capture = @{ schema_version = 1; run_id = $script:RunId; profile_id = 'transport-real'; detail = $script:Detail
        started_utc = $Started; ended_utc = [DateTimeOffset]::UtcNow.ToString('o')
        core = @{ sha = $Spec.core.sha; dirty = $Spec.core.dirty }; harness = @{ sha = $Spec.harness.sha; dirty = $Spec.harness.dirty }
        environment = $environment; launches = $normalized.ToArray(); attempts = @($Attempts); artifacts = @() }
    try {
        foreach ($attempt in $Attempts) { Set-ScanRootKeys $attempt $normalized.ToArray() }
        $capture.artifacts = @(Get-Inventory $script:RunRoot $FixtureRoots | Where-Object { $_.bytes -gt 0 })
        if ($script:Failures.Count -eq 0) { Write-NewJson (Join-Path $script:RunRoot 'collection.json') $capture; return }
    } catch { Add-Failure ('Capture finalization failed: ' + $_.Exception.GetType().Name) }
    try { Write-NewJson (Join-Path $script:RunRoot 'collection-incomplete.json') $capture }
    catch { Add-Failure ('Incomplete capture write failed: ' + $_.Exception.GetType().Name) }
    try { Write-NewJson (Join-Path $script:RunRoot 'failure.json') @{ errors = $script:Failures.ToArray(); retained = $true } }
    catch { Add-Failure ('Failure document write failed: ' + $_.Exception.GetType().Name) }
}

if ($ScenarioMethods.Count -gt 0) {
    Assert-Condition (@($ScenarioMethods | Where-Object { $_ -cnotin $script:Methods }).Count -eq 0 -and
        @($ScenarioMethods | Select-Object -Unique).Count -eq $ScenarioMethods.Count) 'Unknown or duplicate scenario selection.'
    $script:Methods = $ScenarioMethods
}
if ($LibraryOnly) { return }

Assert-Condition ($Config -and $Output) 'Supply -Config and a new -Output directory.'
$spec = Read-Json (Get-FullPath $Config)
Assert-Condition ($spec.schemaVersion -eq 1 -and $spec.detail -cin @('minimal', 'rich')) 'Invalid runner configuration.'
$script:Verified = Assert-Environment $spec.environment
$script:RunRoot = Get-FullPath $Output
Assert-NoLink $script:RunRoot
Assert-Condition (-not (Test-Path -LiteralPath $script:RunRoot)) 'Output already exists.'
$stateRoot = Join-Path $script:Verified.roaming 'GSAnalyzer'
Assert-Condition (-not (Test-Path -LiteralPath $stateRoot)) 'Disposable GSAnalyzer state must be absent before collection.'
Assert-OutputLocation $script:RunRoot $stateRoot @($spec.backend.root, $spec.client.root, $spec.core.root, $spec.harness.root, (Get-FullPath $Config))
$backendInventory = @(Assert-Artifact $spec.backend)
$clientInventory = @(Assert-Artifact $spec.client)
Initialize-NativeLauncher
foreach ($source in @($spec.core, $spec.harness)) {
    Assert-Condition ($source.sha -cmatch '^[a-f0-9]{40}$' -and $source.sourceSha256 -cmatch '^[a-f0-9]{64}$' -and $source.dirty -is [bool]) 'Invalid supplied source identity.'
}
$coreInventory = @(Assert-Source $spec.core)
$harnessInventory = @(Assert-Source $spec.harness)
foreach ($key in @('sdk', 'flutter', 'dart')) { Assert-Condition ($spec.versions.$key.Length -gt 0) 'Missing supplied build-tool version.' }
Assert-Condition ($spec.sentinels.Count -gt 0 -and $spec.sentinels.Count -le 16) 'Supply bounded external sentinel paths.'
foreach ($path in $spec.sentinels) {
    Assert-Condition (-not (Test-Within $script:RunRoot $path) -and -not (Test-Within $path $script:RunRoot) -and
        -not (Test-Within $stateRoot $path) -and -not (Test-Within $path $stateRoot)) 'Sentinel overlaps owned output/state.'
}
Assert-Port
Assert-Condition ([GsScenarioProcess]::HasConsole()) 'Run from a Windows console shared with owned children.'
if ($ValidateOnly) { Write-Output 'Preflight passed; Git source inspection ran; no backend/client created.'; return }

$script:RunId = [Guid]::NewGuid().ToString('N')
$script:Detail = $spec.detail
$script:Launches = New-Object 'System.Collections.Generic.List[object]'
$attempts = New-Object 'System.Collections.Generic.List[object]'
$fixtureRoots = New-Object 'System.Collections.Generic.List[string]'
$before = @()
$started = [DateTimeOffset]::UtcNow.ToString('o')
[void][IO.Directory]::CreateDirectory($script:RunRoot)
$script:Ledger = New-Object IO.StreamWriter([IO.File]::Open((Join-Path $script:RunRoot 'control.jsonl'), 'CreateNew', 'Write', 'Read'), $script:Utf8)
try {
    Write-Ledger 'run.started' @{ environment = $script:Verified; sourceIdentityBasis = 'verified-checkout-and-artifact-sets;build-link-supplied'; sdk = $spec.versions.sdk }
    Write-NewJson (Join-Path $script:RunRoot 'owner.json') @{ schemaVersion = 1; runId = $script:RunId; detail = $script:Detail }
    Write-NewJson (Join-Path $script:RunRoot 'artifact-sets.json') @{ backend = $backendInventory; client = $clientInventory; core = $spec.core; harness = $spec.harness }
    Write-NewJson (Join-Path $script:RunRoot 'source-sets.json') @{ core = $coreInventory; harness = $harnessInventory }
    $before = @(Get-SentinelSnapshot $spec.sentinels)
    Write-NewJson (Join-Path $script:RunRoot 'sentinels-before.json') $before
    Assert-Condition (@($before | Where-Object outcome -eq 'unknown').Count -eq 0) 'Unknown initial sentinel state.'
    foreach ($method in $script:Methods) {
        $attemptId = [Guid]::NewGuid().ToString('N')
        $caseRoot = Join-Path $script:RunRoot ('cases/' + $attemptId)
        $control = Join-Path $caseRoot 'control'
        [void][IO.Directory]::CreateDirectory($control)
        Write-Ledger 'attempt.requested' @{ id = $attemptId; method = $method; ordinal = 1 }
        $primary = Join-Path $caseRoot 'tiny-a'; $secondary = Join-Path $caseRoot 'tiny-b'
        $fixtureRoots.Add($primary); $fixtureRoots.Add($secondary)
        $fixtures = @{ primary = New-Fixture $primary $false; secondary = New-Fixture $secondary $false }
        if ($method -cin @('ActiveCancelRestart', 'QueuedCancelRestart')) {
            $large = Join-Path $caseRoot 'scanner-50k'; $fixtureRoots.Add($large)
            $fixtures.large = New-Fixture $large $true
        }
        Write-NewJson (Join-Path $caseRoot 'fixtures.json') $fixtures
        Assert-Port
        Assert-Condition (-not (Test-Path -LiteralPath $stateRoot)) 'Unarchived product state.'
        [void][IO.Directory]::CreateDirectory($stateRoot)
        Write-NewJson (Join-Path $stateRoot 'benchmark-owner.json') @{ schemaVersion = 1; runId = $script:RunId; attemptId = $attemptId }
        Write-NewJson (Join-Path $stateRoot 'appsettings.user.json') @{
            scan = @{ depth = 50; excludedPaths = @(); followSymlinks = $false; skipHiddenFiles = $true; skipSystemFiles = $true }
            monitoring = @{ enableScheduledScans = $false; ramPollIntervalMs = 1000 }
            cache = @{ scanCacheTtlMinutes = 15; maxCacheScans = 5; maxCachedNodes = 50000 }
            advanced = @{ backendPort = 5200; signalrReconnectDelaysMs = 1000; maxSignalrRetries = 10 }
        }
        Write-NewJson (Join-Path $stateRoot 'automation_rules.json') @()
        Write-NewJson (Join-Path $caseRoot 'state-before.json') @(Get-Inventory $stateRoot)
        $backend = New-Launch 'backend' ([Guid]::NewGuid().ToString('N')) $spec.backend
        Wait-Backend $backend
        $backendIds = New-Object 'System.Collections.Generic.List[string]'; $backendIds.Add($backend.id)
        $clientId = [Guid]::NewGuid().ToString('N')
        $driverConfig = Join-Path $caseRoot 'driver.json'
        Write-NewJson $driverConfig @{ schemaVersion = 1; runId = $script:RunId; launchId = $clientId; attemptId = $attemptId
            method = $method; ordinal = 1; backendLaunchId = $backend.id; controlDirectory = $control; fixtures = $fixtures
            timeoutSeconds = 60; port = 5200 }
        $client = New-Launch 'client' $clientId $spec.client $driverConfig
        $handled = @{}
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not $client.process.Wait(0) -and $clock.Elapsed.TotalSeconds -lt 240) {
            $requests = @(Get-ChildItem -LiteralPath $control -Filter '*.request.json')
            Assert-Condition ($requests.Count -le 1) 'Scenario command count exceeded.'
            foreach ($file in $requests) {
                if ($handled.ContainsKey($file.Name)) { continue }
                $request = Read-Json $file.FullName
                Assert-Condition ($request.schemaVersion -eq 1 -and $request.runId -ceq $script:RunId -and $request.attemptId -ceq $attemptId -and
                    $request.commandId -cmatch '^[a-f0-9]{32}$' -and $file.Name -ceq ($request.commandId + '.request.json')) 'Control ownership mismatch.'
                Assert-ControlAction $method $request.action $handled.Count $clock.Elapsed.TotalSeconds
                $handled[$file.Name] = $true
                Write-Ledger 'control.received' $request
                if ($request.action -ceq 'restart') {
                    Assert-Condition ($request.oldLaunchId -ceq $backend.id -and $request.newLaunchId -cmatch '^[a-f0-9]{32}$' -and
                        -not ($script:Launches.id -ccontains $request.newLaunchId)) 'Restart identity mismatch.'
                    Assert-Port $backend.process.Pid
                    Stop-Launch $backend $true
                    Assert-Condition ($backend.outcome -ceq 'exited') 'Backend restart did not stop gracefully.'
                    Write-NewJson (Join-Path $control ($request.commandId + '.state.json')) @(Get-Inventory $stateRoot)
                    Assert-Port
                    $backend = New-Launch 'backend' $request.newLaunchId $spec.backend
                    $backendIds.Add($backend.id)
                    Wait-Backend $backend
                } elseif ($request.action -ceq 'mutate') {
                    $target = Join-Path $fixtures.primary.root 'A/a.txt'
                    Assert-NoLink $target
                    Assert-Condition ((Get-Item -LiteralPath $target).Length -eq 5) 'Mutation precondition failed.'
                    [IO.File]::WriteAllBytes($target, (New-Object byte[] 13))
                    Write-NewJson (Join-Path $control ($request.commandId + '.fixture.json')) @(Get-Inventory $fixtures.primary.root)
                } else { throw 'Unknown control action.' }
                Write-NewJson (Join-Path $control ($request.commandId + '.ack.json')) @{ schemaVersion = 1; runId = $script:RunId
                    commandId = $request.commandId; attemptId = $attemptId; outcome = 'ready'; backendLaunchId = $backend.id }
                Write-Ledger 'control.acknowledged' @{ commandId = $request.commandId; backendLaunchId = $backend.id }
            }
            Start-Sleep -Milliseconds 25
        }
        Stop-Launch $client $false
        Stop-Launch $backend $true
        Assert-Port
        $resultPath = Join-Path $control 'result.json'
        Assert-Condition (Test-Path -LiteralPath $resultPath) 'Driver result unavailable; retain raw launch ledger.'
        $result = Read-Json $resultPath
        Assert-Condition ($result.id -ceq $attemptId -and $result.method -ceq $method -and $result.ordinal -eq 1 -and
            $result.client_launch -ceq $clientId -and ($result.backend_launches -join ',') -ceq ($backendIds -join ',')) 'Driver result identity mismatch.'
        $attempts.Add($result)
        Write-Ledger 'attempt.finished' $result
        Assert-NoLink $stateRoot
        $stateOwner = Read-Json (Join-Path $stateRoot 'benchmark-owner.json')
        Assert-Condition ($stateOwner.runId -ceq $script:RunId -and $stateOwner.attemptId -ceq $attemptId) 'Product-state ownership changed.'
        [IO.Directory]::Move($stateRoot, (Join-Path $caseRoot 'state-after'))
        $after = @(Get-SentinelSnapshot $spec.sentinels)
        Write-NewJson (Join-Path $caseRoot 'sentinels-after.json') $after
        Assert-SentinelsUnchanged $before $after
    }
} catch {
    Add-Failure ($_.Exception.GetType().Name + ': ' + $_.Exception.Message)
    Write-CleanupLedger 'run.failed' @{ errors = $script:Failures.ToArray() }
} finally {
    Close-OwnedLaunches $script:Launches
    try {
        $after = @(Get-SentinelSnapshot $spec.sentinels)
        Write-NewJson (Join-Path $script:RunRoot 'sentinels-after.json') $after
        Assert-SentinelsUnchanged $before $after
    } catch { Add-Failure ('Final sentinel verification failed: ' + $_.Exception.GetType().Name) }
    finally {
        try { $script:Ledger.Dispose() }
        catch { Add-Failure ('Ledger close failed: ' + $_.Exception.GetType().Name) }
    }
}

Complete-Capture $spec $started $attempts.ToArray() $fixtureRoots.ToArray()
if ($script:Failures.Count) {
    Write-Error ($script:Failures -join '; ') -ErrorAction Continue
    exit 1
}
Write-Output ('Capture retained: ' + $script:RunRoot)
Write-Output 'Import this capture for correctness/lifetime evaluation; launcher exit is not product acceptance.'
