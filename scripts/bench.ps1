<#
.SYNOPSIS
Measures Loom's start time, idle CPU, frame rate and memory in a bare instance.

.DESCRIPTION
Starts the built Loom.exe with --bare in a temporary folder seeded with a generated data folder: three projects that are
git repositories, a long chat with Markdown, code, tables and tool calls, and the layouts each scenario needs. The chat's
agent is the test fixture (Lokn.Tests.exe), which can keep a turn running: silently ('busy') or streaming an answer.

Loom writes startup phases and presented frames per second to the file LOKN_PERF_TRACE names (Shell/PerfTrace.cs). For
each scenario the script reads the process's CPU time and the frames presented over -Seconds, and prints a table:

  start      cold start to first frame and to interactive (median of -Starts launches)
  home       Home, idle, focused
  chat       a long chat, idle, focused
  working    the chat while its agent works silently, focused
  streaming  the chat while its agent streams an answer, focused
  unfocused  working, with another window focused beside it
  covered    working, under a window that covers it
  minimized  working, minimized
  settings   the Settings panel, focused
  scroll     the long chat scrolled by the wheel as Goo input: frames and the gaps between them after a notch and a flick, and Goo's frame times
  memory     working set and private bytes after start and after -MemoryMinutes, with chat, Git and terminal panels

Loom opens on Home and puts the restored layout back into its session, so the chat, working, streaming and settings launches open
their view with --open-link. The windows it opens take focus for the focused scenarios; it closes them itself.

It only ever runs bare instances, and guards the user's own Loom around every launch:

- It refuses an -Exe from the user's install (%LOCALAPPDATA%\Loom) and a build without bare isolation (Lokn.BareInstance,
  Loom 0.4.4 and older), whose --bare window stopped the user's daemon and took over their protocols and sign-in entry.
- Before each launch it records the user's daemons (Loom.exe --host without --bare), HKCU\Software\Classes\loom, \lokn and
  \lokn-notification, the Run key's Loom and LoomHost values, the Start menu's Loom.lnk and the end of Velopack's
  velopack.log. After the launch it checks each, and that no Loom.exe it started ran Velopack; on any change it stops with
  ISOLATION BREACH and names what changed. It never writes them itself.
- Each launch runs in its own job object, so it stops only the processes it started and their children, never a process by
  name; any Loom.exe among them without --bare <its folder> is a breach too. The job ends its processes if this script dies.
- Goo's pipeline cache goes to the bench folder, and Goo's single-instance lock file for the bare app ID is removed afterwards.

It never touches %LOCALAPPDATA%\Lokn. Prints BENCH OK or BENCH FAILED last.

.EXAMPLE
pwsh scripts/bench.ps1

.EXAMPLE
pwsh scripts/bench.ps1 -Exe $env:TEMP\loom-perf\after\Loom.exe -Label after -Out docs\bench-after.json
#>
[CmdletBinding( PositionalBinding = $false )]
param(
	# The Loom.exe to measure. Defaults to this checkout's Release build.
	[string] $Exe,

	# The fixture agent, Lokn.Tests.exe. Defaults to this checkout's Release test build.
	[string] $Fixture,

	# The scenarios to run, in order.
	[ValidateSet( 'start', 'home', 'chat', 'working', 'streaming', 'unfocused', 'covered', 'minimized', 'settings', 'scroll', 'memory', 'switch' )]
	[string[]] $Scenario = @( 'start', 'home', 'chat', 'working', 'streaming', 'unfocused', 'covered', 'minimized', 'settings', 'scroll', 'memory' ),

	# Seconds each CPU and frame measurement lasts.
	[int] $Seconds = 20,

	# Launches the start measurement takes its median over.
	[int] $Starts = 5,

	# Minutes the memory scenario waits before its second reading.
	[double] $MemoryMinutes = 2,

	# A name for this run in the table and the results file, such as before or after.
	[string] $Label = 'run',

	# A JSON file the results are written to.
	[string] $Out,

	# The id of one of the user's own chats (a file name under %LOCALAPPDATA%\Lokn\conversations, without .json) whose copy replaces the
	# generated long chat, so the chat scenarios measure a real transcript. The user's file is only read.
	[string] $RealChat,

	# Wheel steps the scroll scenario turns instead of its notch and flick, each <notches>:<count>, such as 3:20 for twenty quick turns of
	# three notches up; repeat a step to scroll further through a long chat.
	[string[]] $ScrollSteps,

	# Records a dotnet-trace CPU sample of Loom while the scroll scenario scrolls (dotnet-trace must be installed).
	[switch] $CpuTrace,

	# Runs the scroll scenario without Goo's frame profile, whose console output can itself stall frames.
	[switch] $NoProfile
)

. "$PSScriptRoot\common.ps1"

if ( -not $Exe )
{
	$Exe = Join-Path $RepoRoot 'bin\Release\net10.0\Loom.exe'
}

if ( -not $Fixture )
{
	$Fixture = Join-Path $RepoRoot 'tests\Lokn.Tests\bin\Release\net10.0\Lokn.Tests.exe'
}

foreach ( $file in @( $Exe, $Fixture ) )
{
	if ( -not ( Test-Path $file ) )
	{
		Write-Host "Missing $file; run scripts/build.ps1 first."
		Write-Host 'BENCH FAILED'
		exit 1
	}
}

$Exe = ( Resolve-Path $Exe ).Path
$Fixture = ( Resolve-Path $Fixture ).Path

# The user's own Loom: its install, and what a bare instance must never change.
$UserInstall = Join-Path $env:LOCALAPPDATA 'Loom'
$GuardedKeys = @( 'Software\Classes\loom', 'Software\Classes\lokn', 'Software\Classes\lokn-notification' )
$RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$RunValues = @( 'Loom', 'LoomHost' )
$Shortcut = Join-Path ( [Environment]::GetFolderPath( 'Programs' ) ) 'Loom.lnk'
$VelopackLog = Join-Path $env:LOCALAPPDATA 'velopack\velopack.log'

if ( $Exe.StartsWith( $UserInstall + '\', [StringComparison]::OrdinalIgnoreCase ) )
{
	Write-Host "Refusing to run the user's installed Loom ($Exe); copy a build elsewhere first."
	Write-Host 'BENCH FAILED'
	exit 1
}

# A build without bare isolation opens a --bare window on the user's daemon, protocols and sign-in entry.
$library = Join-Path ( Split-Path -Parent $Exe ) 'Loom.dll'
if ( -not ( Test-Path $library ) -or -not ( [Text.Encoding]::ASCII.GetString( [IO.File]::ReadAllBytes( $library ) ).Contains( 'BareInstance' ) ) )
{
	Write-Host "Refusing $Exe`: its Loom.dll has no bare isolation (Lokn.BareInstance), so a --bare instance would change the user's Loom."
	Write-Host 'BENCH FAILED'
	exit 1
}

$Root = Join-Path ( [IO.Path]::GetTempPath() ) "loom-bench\$Label-$( Get-Date -Format 'yyyyMMdd-HHmmss' )"
New-Item -ItemType Directory -Force $Root | Out-Null

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Windows.Forms.Primitives, System.Drawing, System.Drawing.Primitives, System.ComponentModel.Primitives, System.Diagnostics.Process, Microsoft.Win32.Primitives, System.Threading, System.Threading.Thread -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public sealed class BenchJob : IDisposable
{
	[DllImport( "kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true )] private static extern IntPtr CreateJobObject( IntPtr attributes, string name );
	[DllImport( "kernel32.dll", SetLastError = true )] private static extern bool SetInformationJobObject( IntPtr job, int infoClass, ref ExtendedLimits info, int length );
	[DllImport( "kernel32.dll", SetLastError = true )] private static extern bool AssignProcessToJobObject( IntPtr job, IntPtr process );
	[DllImport( "kernel32.dll", SetLastError = true )] private static extern bool TerminateJobObject( IntPtr job, uint exitCode );
	[DllImport( "kernel32.dll", SetLastError = true )] private static extern bool QueryInformationJobObject( IntPtr job, int infoClass, IntPtr info, int length, IntPtr returned );
	[DllImport( "kernel32.dll" )] private static extern bool CloseHandle( IntPtr handle );

	[StructLayout( LayoutKind.Sequential )]
	private struct BasicLimits
	{
		public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass, SchedulingClass;
	}

	[StructLayout( LayoutKind.Sequential )]
	private struct ExtendedLimits
	{
		public BasicLimits Basic;
		public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
		public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
	}

	private IntPtr handle;

	// The job ends every process in it once its last handle closes, so nothing outlives this script.
	public BenchJob()
	{
		handle = CreateJobObject( IntPtr.Zero, null );
		if ( handle == IntPtr.Zero )
			throw new Win32Exception();
		var limits = new ExtendedLimits();
		limits.Basic.LimitFlags = 0x2000;
		if ( !SetInformationJobObject( handle, 9, ref limits, Marshal.SizeOf( typeof( ExtendedLimits ) ) ) )
			throw new Win32Exception();
	}

	// Children a process starts after this join the job with it.
	public void Add( Process process )
	{
		if ( !AssignProcessToJobObject( handle, process.Handle ) )
			throw new Win32Exception();
	}

	public int[] ListProcessIds()
	{
		const int capacity = 4096;
		var size = 8 + capacity * IntPtr.Size;
		var buffer = Marshal.AllocHGlobal( size );
		try
		{
			if ( !QueryInformationJobObject( handle, 3, buffer, size, IntPtr.Zero ) )
				throw new Win32Exception();
			var count = Marshal.ReadInt32( buffer, 4 );
			var ids = new int[count];
			for ( var index = 0; index < count; index++ )
				ids[index] = (int)Marshal.ReadIntPtr( buffer, 8 + index * IntPtr.Size ).ToInt64();
			return ids;
		}
		finally
		{
			Marshal.FreeHGlobal( buffer );
		}
	}

	public void Terminate()
	{
		TerminateJobObject( handle, 1 );
	}

	public void Dispose()
	{
		if ( handle == IntPtr.Zero )
			return;
		CloseHandle( handle );
		handle = IntPtr.Zero;
	}
}

public static class BenchWindow
{
	[DllImport( "user32.dll" )] private static extern bool SetForegroundWindow( IntPtr window );
	[DllImport( "user32.dll" )] private static extern IntPtr GetForegroundWindow();
	[DllImport( "user32.dll" )] private static extern uint GetWindowThreadProcessId( IntPtr window, out uint process );
	[DllImport( "user32.dll" )] private static extern bool AttachThreadInput( uint from, uint to, bool attach );
	[DllImport( "kernel32.dll" )] private static extern uint GetCurrentThreadId();
	[DllImport( "user32.dll" )] private static extern bool ShowWindow( IntPtr window, int command );
	[DllImport( "user32.dll" )] private static extern bool IsIconic( IntPtr window );
	[DllImport( "user32.dll" )] private static extern bool BringWindowToTop( IntPtr window );
	[DllImport( "user32.dll" )] private static extern bool SetWindowPos( IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags );
	[DllImport( "user32.dll" )] private static extern bool GetWindowRect( IntPtr window, out Rect rect );

	[StructLayout( LayoutKind.Sequential )]
	private struct Rect
	{
		public int Left, Top, Right, Bottom;
	}

	private static Form cover;

	public static int[] GetBounds( IntPtr window )
	{
		Rect rect;
		GetWindowRect( window, out rect );
		return new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
	}

	public static void Place( IntPtr window, int x, int y, int width, int height )
	{
		SetWindowPos( window, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010 );
	}

	public static void Minimize( IntPtr window )
	{
		ShowWindow( window, 6 );
	}

	public static bool Focus( IntPtr window )
	{
		if ( IsIconic( window ) )
			ShowWindow( window, 9 );
		uint ignored;
		var foreground = GetWindowThreadProcessId( GetForegroundWindow(), out ignored );
		var self = GetCurrentThreadId();
		AttachThreadInput( self, foreground, true );
		BringWindowToTop( window );
		var focused = SetForegroundWindow( window );
		AttachThreadInput( self, foreground, false );
		return focused;
	}

	public static IntPtr ShowCover( int x, int y, int width, int height )
	{
		var ready = new ManualResetEventSlim();
		var thread = new Thread( () =>
		{
			cover = new Form
			{
				FormBorderStyle = FormBorderStyle.None,
				StartPosition = FormStartPosition.Manual,
				Bounds = new Rectangle( x, y, width, height ),
				TopMost = true,
				ShowInTaskbar = false,
				BackColor = Color.DimGray,
				Text = "Loom bench",
			};
			cover.Shown += ( sender, args ) => ready.Set();
			Application.Run( cover );
		} );
		thread.SetApartmentState( ApartmentState.STA );
		thread.IsBackground = true;
		thread.Start();
		ready.Wait();
		var handle = IntPtr.Zero;
		cover.Invoke( (Action)( () => handle = cover.Handle ) );
		Focus( handle );
		return handle;
	}

	public static void CloseCover()
	{
		if ( cover == null )
			return;
		cover.Invoke( (Action)( () => cover.Close() ) );
		cover = null;
	}
}
'@

# ---------------------------------------------------------------------------------------------------------------------
# The data folder

# Writes an object as JSON without a byte order mark.
function Write-Json( [string] $Path, $Value )
{
	$directory = Split-Path -Parent $Path
	New-Item -ItemType Directory -Force $directory | Out-Null
	[IO.File]::WriteAllText( $Path, ( $Value | ConvertTo-Json -Depth 32 ), [Text.UTF8Encoding]::new( $false ) )
}

# Creates a git repository with a few files and commits.
function New-Repository( [string] $Path, [string] $Name )
{
	New-Item -ItemType Directory -Force "$Path\src" | Out-Null
	git -C $Path init -q -b main 2>&1 | Out-Null
	git -C $Path config user.email 'bench@example.com'
	git -C $Path config user.name 'Bench'
	for ( $commit = 1; $commit -le 12; $commit++ )
	{
		$lines = 1..( 20 + $commit ) | ForEach-Object { "	public int Value$_ => $_ * $commit;" }
		Set-Content "$Path\src\File$( $commit % 4 ).cs" -Value ( @( "namespace $Name;", '', "public sealed class File$commit", '{' ) + $lines + '}' )
		git -C $Path add -A 2>&1 | Out-Null
		git -C $Path commit -q -m "Change $commit of $Name" 2>&1 | Out-Null
	}

	# A change in progress, so the Git panel and the changes view have something to show.
	Add-Content "$Path\src\File1.cs" '// work in progress'
	Set-Content "$Path\README.md" "# $Name"
}

# Builds one exchange of the long chat: a question, a tool call and its result, and an answer with Markdown.
function New-Exchange( [int] $Index, [datetime] $At, [int] $OutputLines = 12 )
{
	$time = [DateTimeOffset]$At
	$stamp = $time.ToUnixTimeMilliseconds()
	$callId = "call-$Index"
	$question = "Step ${Index}: tidy the parser and explain what changed in **module $Index**."
	$command = "rg -n `"Parse`" src/Parser$Index.cs"
	$output = ( 1..$OutputLines | ForEach-Object { "src/Parser$Index.cs:$( $_ * 7 ):    var token$_ = Parse( input, $_ );" } ) -join "`n"
	$answer = @"
## Parser $Index

The parser read each token twice. It now reads it once and keeps the position, which also fixes the off-by-one in
error messages. Three things changed:

- ``Parse`` takes a span instead of a string.
- **Errors** report the column where the token starts.
- The tokenizer no longer allocates for whitespace.

| File | Before | After |
|---|---|---|
| Parser$Index.cs | 412 lines | 377 lines |
| Tokenizer.cs | 210 lines | 198 lines |
| ParserTests.cs | 18 tests | 24 tests |

``````csharp
public static Token Parse( ReadOnlySpan<char> input, ref int position )
{
	var start = position;
	while ( position < input.Length && char.IsWhiteSpace( input[position] ) )
		position++;
	return new Token( input[start..position], start );
}
``````

Run ``dotnet test`` to check it; all $( 20 + $Index ) tests pass.
"@
	$user = @{ role = 'user'; content = @( @{ type = 'text'; text = $question } ); timestamp = $stamp }
	$call = @{
		role = 'assistant'
		content = @( @{ type = 'text'; text = 'Let me look at the parser first.' }, @{ type = 'toolCall'; id = $callId; name = 'bash'; arguments = @{ command = $command } } )
		api = 'anthropic-messages'; provider = 'fixture'; model = 'one'
		usage = @{ input = 1200; output = 180; cacheRead = 0; cacheWrite = 0; totalTokens = 1380 }
		stopReason = 'toolUse'; timestamp = $stamp + 1000
	}
	$result = @{ role = 'toolResult'; toolCallId = $callId; toolName = 'bash'; content = @( @{ type = 'text'; text = $output } ); isError = $false; timestamp = $stamp + 2000 }
	$reply = @{
		role = 'assistant'
		content = @( @{ type = 'text'; text = $answer } )
		api = 'anthropic-messages'; provider = 'fixture'; model = 'one'
		usage = @{ input = 2400; output = 620; cacheRead = 0; cacheWrite = 0; totalTokens = 3020 }
		stopReason = 'stop'; timestamp = $stamp + 3000
	}

	$messages = @()
	$offset = 0
	foreach ( $pair in @( @( 'user', $question, $user ), @( 'assistant', 'Let me look at the parser first.', $call ), @( 'toolResult', $output, $result ), @( 'assistant', $answer, $reply ) ) )
	{
		$messages += @{ Id = [guid]::NewGuid().ToString(); Role = $pair[0]; Text = $pair[1]; CreatedAt = $time.AddSeconds( $offset ).ToString( 'o' ); RichContent = $pair[2] }
		$offset++
	}

	return $messages
}

# Writes the data folder every scenario starts from: settings, modules, projects and chats; each run adds its layout.
function New-Template( [string] $Path )
{
	$projects = @()
	foreach ( $name in @( 'Atlas', 'Beacon', 'Compass', 'Diary' ) )
	{
		$directory = Join-Path $Root "projects\$name"
		New-Repository $directory $name
		# Diary is the Personal profile's, so the switch scenario has two profiles to switch between.
		$projects += @{ Id = [guid]::NewGuid().ToString(); Profile = if ( $name -eq 'Diary' ) { 'Personal' } else { 'Work' }; Name = $name; Directory = $directory }
	}

	Write-Json "$Path\projects.json" @{ Version = 1; Projects = $projects }
	$global = @{ 'omp.executable' = $Fixture; 'terminal.shell' = 'cmd.exe' }
	# A real chat is measured as the user sees it: in their theme and fonts.
	if ( $RealChat )
	{
		$user = Get-Content -Raw ( Join-Path $env:LOCALAPPDATA 'Lokn\settings.json' ) | ConvertFrom-Json
		foreach ( $setting in $user.Global.PSObject.Properties | Where-Object { $_.Name -like 'theme.*' } )
		{
			$global[$setting.Name] = $setting.Value
		}
	}
	Write-Json "$Path\settings.json" @{
		Version = 1
		ActiveProfile = 'Work'
		Global = $global
		Work = @{}
		Personal = @{}
	}
	# A bare instance starts with every module off; GitHub stays off, as it asks GitHub over the network.
	Write-Json "$Path\modules.json" @{
		On = @( 'browser', 'conversations', 'editor', 'files', 'git', 'markdown', 'notifications', 'omp', 'projects', 'remote', 'setup', 'terminal', 'workspaces', 'worktrees' )
		Off = @( 'github' )
	}

	$now = [datetime]::UtcNow
	$conversations = @()
	$chats = @{}
	for ( $chat = 0; $chat -lt 12; $chat++ )
	{
		$id = [guid]::NewGuid()
		# Chat 8 is as long as the longest chats people keep (about 1,400 messages), for the switch scenario.
		$exchanges = if ( $chat -eq 0 ) { 60 } elseif ( $chat -eq 8 ) { 350 } else { 2 }
		$messages = @()
		for ( $exchange = 1; $exchange -le $exchanges; $exchange++ )
		{
			# Chat 8's tool outputs are as long as real ones (about 17 KB a message), so its file is tens of megabytes like the longest real chats.
			$messages += New-Exchange $exchange $now.AddHours( -$chat ).AddMinutes( $exchange - $exchanges ) $( if ( $chat -eq 8 ) { 300 } else { 12 } )
		}

		# The last three chats are Diary's, in the Personal profile; the others go round the Work projects.
		$project = if ( $chat -ge 9 ) { $projects[3] } else { $projects[$chat % 3] }
		$conversations += @{
			Id = $id.ToString(); Profile = $project.Profile; ProjectId = $project.Id; Title = if ( $chat -eq 0 ) { 'Tidy the parser' } else { "Chat $chat" }
			UpdatedAt = $now.AddHours( -$chat ).ToString( 'o' ); CreatedAt = $now.AddHours( -$chat - 1 ).ToString( 'o' )
			BackendSessionPath = $null; Draft = $null; IsPinned = $false; IsTitleChosen = $true; IsTitleGenerated = $false; HasChat = $true
			Directory = $null; Worktree = $null; IsTaskCheckOff = $false; Position = $null
		}
		$file = "$Path\conversations\$( $id.ToString( 'N' ) ).json"
		# -RealChat copies one of the user's own chats in place of the generated long one; the user's file is only read.
		if ( $chat -eq 0 -and $RealChat )
		{
			# The text is copied as written, only its id swapped, so no JSON round trip changes the rich payloads the chat draws.
			$source = if ( Test-Path -LiteralPath $RealChat -PathType Leaf ) { $RealChat } else { Join-Path $env:LOCALAPPDATA "Lokn\conversations\$RealChat.json" }
			$real = Get-Content -Raw $source
			$real = [regex]::new( '"ConversationId"\s*:\s*"[^"]*"' ).Replace( $real, "`"ConversationId`": `"$id`"", 1 )
			New-Item -ItemType Directory -Force ( Split-Path -Parent $file ) | Out-Null
			[IO.File]::WriteAllText( $file, $real, [Text.UTF8Encoding]::new( $false ) )
		}
		else
		{
			Write-Json $file @{ Version = 2; ConversationId = $id.ToString(); Messages = $messages }
		}
		$chats[$chat] = $id
	}

	Write-Json "$Path\conversations.json" @{ Version = 2; Conversations = $conversations }
	return @{ Chat = $chats[0]; Chats = $chats; Project = $projects[0] }
}

# Builds a saved layout: panes side by side, each with one panel.
function New-Layout( [object[]] $Panels )
{
	$entries = @()
	$panes = @()
	foreach ( $panel in $Panels )
	{
		$panelId = [guid]::NewGuid().ToString()
		$entries += @{ Id = $panelId; TypeId = $panel.TypeId; Title = $panel.Title; State = $panel.State }
		$panes += @{ Kind = 'pane'; Id = [guid]::NewGuid().ToString(); PanelId = $panelId; Axis = 0; Ratio = 0; First = $null; Second = $null }
	}

	if ( $panes.Count -eq 0 )
	{
		$panes = @( @{ Kind = 'pane'; Id = [guid]::NewGuid().ToString(); PanelId = $null; Axis = 0; Ratio = 0; First = $null; Second = $null } )
	}

	$root = $panes[0]
	for ( $index = 1; $index -lt $panes.Count; $index++ )
	{
		$root = @{ Kind = 'split'; Id = [guid]::NewGuid().ToString(); PanelId = $null; Axis = 0; Ratio = if ( $index -eq 1 ) { 0.6 } else { 0.7 }; First = $root; Second = $panes[$index] }
	}

	return @{ Version = 1; ActivePaneId = $panes[0].Id; Root = $root; Panels = $entries }
}

# Builds the layout a scenario starts with.
function Get-Layout( [string] $Name )
{
	$chat = @{ TypeId = 'conversations.chat'; Title = 'Tidy the parser'; State = @{ ConversationId = $Seed.Chat.ToString(); Profile = 0; ContinueTurn = $false } }
	switch ( $Name )
	{
		'home' { return New-Layout @() }
		'chat' { return New-Layout @( $chat ) }
		# A chat a reload stopped mid-turn asks its agent to carry on when it opens, which starts the fixture's long turn.
		'working' { $chat.State.ContinueTurn = $true; return New-Layout @( $chat ) }
		'settings' { return New-Layout @( @{ TypeId = 'shell.settings'; Title = 'Settings'; State = $null } ) }
		'workspace'
		{
			return New-Layout @(
				$chat,
				@{ TypeId = 'git.panel'; Title = 'Git'; State = @{ Directory = $Seed.Project.Directory; ReviewAction = $null; ReviewId = $null; Focus = $null; Staged = $false } },
				@{ TypeId = 'terminal.shell'; Title = 'Terminal'; State = @{ Name = 'cmd.exe'; Command = 'cmd.exe'; Directory = $Seed.Project.Directory } } )
		}
	}
}

# Gets the link a scenario's launch opens once Loom has started, or nothing for Home and the layouts measured as they start.
function Get-Link( [string] $Name )
{
	switch ( $Name )
	{
		{ $_ -in 'chat', 'working' } { return "lokn://conversations/open/$( $Seed.Chat.ToString( 'N' ) )" }
		'settings' { return 'lokn://shell/settings' }
	}
}

# ---------------------------------------------------------------------------------------------------------------------
# Guarding the user's Loom

# Reads a registry key under HKCU and every key and value below it; an empty list when the key is missing.
function Read-RegistryTree( [string] $Path )
{
	$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey( $Path )
	if ( -not $key )
	{
		return @()
	}

	try
	{
		$entries = @( [pscustomobject]@{ Key = $Path; Name = $null; Kind = $null; Value = $null; Text = "$Path\" } )
		foreach ( $name in $key.GetValueNames() )
		{
			$kind = $key.GetValueKind( $name )
			$value = $key.GetValue( $name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames )
			$text = if ( $value -is [byte[]] ) { [Convert]::ToBase64String( $value ) } elseif ( $value -is [string[]] ) { $value -join "`0" } else { "$value" }
			$entries += [pscustomobject]@{ Key = $Path; Name = $name; Kind = $kind; Value = $value; Text = "$Path\[$name] $kind = $text" }
		}
		foreach ( $child in $key.GetSubKeyNames() )
		{
			$entries += Read-RegistryTree "$Path\$child"
		}
		return $entries
	}
	finally
	{
		$key.Dispose()
	}
}

# Lists the user's daemons, Loom.exe --host without --bare from any folder, by process ID, start time and command line.
function Get-UserDaemons
{
	@( Get-CimInstance Win32_Process -Filter "Name = 'Loom.exe'" | Where-Object { $_.CommandLine -match '(^|\s)--host(\s|$)' -and $_.CommandLine -notmatch '(^|\s)--bare(\s|$)' } |
		ForEach-Object { "$( $_.ProcessId ) started $( $_.CreationDate.ToUniversalTime().ToString( 'o' ) ): $( $_.CommandLine )" } | Sort-Object )
}

# Records what a bare launch must leave as it is: the user's daemons, protocols, sign-in entries, Start menu shortcut and Velopack's log.
function Get-Guard
{
	$registry = [ordered]@{}
	foreach ( $path in $GuardedKeys )
	{
		$registry[$path] = @( Read-RegistryTree $path )
	}

	$run = [ordered]@{}
	$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey( $RunKey )
	try
	{
		foreach ( $name in $RunValues )
		{
			$run[$name] = if ( $key -and $null -ne $key.GetValue( $name ) ) { [pscustomobject]@{ Kind = $key.GetValueKind( $name ); Value = $key.GetValue( $name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames ) } } else { $null }
		}
	}
	finally
	{
		if ( $key ) { $key.Dispose() }
	}

	return [pscustomobject]@{
		Daemons = Get-UserDaemons
		Registry = $registry
		Run = $run
		Shortcut = if ( Test-Path -LiteralPath $Shortcut ) { [IO.File]::ReadAllBytes( $Shortcut ) } else { $null }
		Velopack = if ( Test-Path -LiteralPath $VelopackLog ) { ( Get-Item -LiteralPath $VelopackLog ).Length } else { 0 }
	}
}

# Reads what Velopack's shared log gained since a length; other Velopack apps may write it too, so callers look for their own process IDs.
function Read-VelopackSince( [long] $Length )
{
	if ( -not ( Test-Path -LiteralPath $VelopackLog ) )
	{
		return ''
	}

	$stream = [IO.FileStream]::new( $VelopackLog, 'Open', 'Read', 'ReadWrite, Delete' )
	try
	{
		$stream.Position = [math]::Min( $Length, $stream.Length )
		return [IO.StreamReader]::new( $stream ).ReadToEnd()
	}
	finally
	{
		$stream.Dispose()
	}
}

# Compares the user's Loom with what Get-Guard recorded before a launch, and checks that none of the launch's Loom.exe processes ran Velopack, then throws
# on any difference. It only reports: it never writes the user's registry, Run key or shortcut, since a restore from a wrong snapshot once emptied the
# user's link handlers; the user's Loom is repaired from Loom itself.
function Assert-Guard( $Before, [string] $Launch, [string[]] $Breaches = @(), [int[]] $LoomIds = @() )
{
	$after = Get-Guard
	$problems = @( $Breaches )
	if ( ( $Before.Daemons -join "`n" ) -ne ( $after.Daemons -join "`n" ) )
	{
		$problems += "the user's daemons changed from [$( $Before.Daemons -join '; ' )] to [$( $after.Daemons -join '; ' )]; start the user's daemon again from Loom if it stopped"
	}

	foreach ( $path in $GuardedKeys )
	{
		$was = @( $Before.Registry[$path] | ForEach-Object Text ) -join "`n"
		$now = @( $after.Registry[$path] | ForEach-Object Text ) -join "`n"
		if ( $was -ne $now )
		{
			$problems += "HKCU\$path changed: was [$( $was -replace "`n", '; ' )], is now [$( $now -replace "`n", '; ' )]"
		}
	}

	foreach ( $name in $RunValues )
	{
		$was = $Before.Run[$name]
		$now = $after.Run[$name]
		if ( "$( $was.Kind ) $( $was.Value )" -ne "$( $now.Kind ) $( $now.Value )" )
		{
			$problems += "HKCU\$RunKey\$name changed: was [$( $was.Value )], is now [$( $now.Value )]"
		}
	}

	$wasShortcut = if ( $Before.Shortcut ) { [Convert]::ToBase64String( $Before.Shortcut ) } else { '' }
	$nowShortcut = if ( $after.Shortcut ) { [Convert]::ToBase64String( $after.Shortcut ) } else { '' }
	if ( $wasShortcut -ne $nowShortcut )
	{
		$problems += "the Start menu shortcut $Shortcut changed"
	}

	$velopack = Read-VelopackSince $Before.Velopack
	foreach ( $id in $LoomIds )
	{
		if ( $velopack.Contains( "[lib-csharp:$id]" ) )
		{
			$problems += "Loom.exe process $id ran Velopack (see $VelopackLog), which a bare instance must never do"
		}
	}

	if ( $problems.Count )
	{
		throw "ISOLATION BREACH in $Launch`: $( $problems -join ' | ' )"
	}
}

# Computes the file Goo's single-instance election leaves in %LOCALAPPDATA%\goo for a bare folder's app ID (BareInstance.ComputeApplicationId).
function Get-GooLock( [string] $Folder )
{
	$folderHash = [Convert]::ToHexString( [Security.Cryptography.SHA256]::HashData( [Text.Encoding]::UTF8.GetBytes( $Folder.ToUpperInvariant() ) ) ).Substring( 0, 8 ).ToLowerInvariant()
	$identity = "$( [Security.Principal.WindowsIdentity]::GetCurrent().User.Value ):$( ( Get-Process -Id $PID ).SessionId )"
	$lockHash = [Convert]::ToHexString( [Security.Cryptography.SHA256]::HashData( [Text.Encoding]::UTF8.GetBytes( "dev.lokn.desktop.bare.$folderHash`n$identity" ) ), 0, 16 )
	return Join-Path $env:LOCALAPPDATA "goo\$lockHash.lock"
}

# ---------------------------------------------------------------------------------------------------------------------
# Running Loom

# Starts Loom on a fresh copy of the template with a layout, in its own job, after recording the user's Loom; returns once it is interactive and its
# window placed. A launch that fails is stopped and checked before the error goes on.
function Start-Loom( [string] $Layout, [string] $Turn, [switch] $Profile, [string] $Drive )
{
	$script:RunCount++
	$folder = Join-Path $Root "run-$script:RunCount-$Layout"
	Copy-Item -Recurse $Template $folder
	Write-Json "$folder\workspace.json" ( Get-Layout $Layout )
	$trace = Join-Path $folder 'perf.log'
	$info = [Diagnostics.ProcessStartInfo]::new( $Exe )
	$info.ArgumentList.Add( '--bare' )
	$info.ArgumentList.Add( $folder )
	# Loom opens on Home and puts the restored layout back into its session, so a scenario's own view is opened by the link it passes.
	if ( Get-Link $Layout )
	{
		$info.ArgumentList.Add( '--open-link' )
		$info.ArgumentList.Add( ( Get-Link $Layout ) )
	}
	$info.UseShellExecute = $false
	$info.Environment['LOKN_PERF_TRACE'] = $trace
	$info.Environment['LOKN_FIXTURE_TURN'] = $Turn
	$info.Environment['LOKN_FIXTURE_STREAM_MS'] = '400'
	# One warm pipeline cache for every launch, outside the user's.
	$info.Environment['GOO_VK_PIPELINE_CACHE_DIR'] = Join-Path $Root 'goo-pipeline-cache'
	# The switches PerfTrace runs and measures in the switch scenario.
	$info.Environment['LOKN_PERF_DRIVE'] = $Drive
	if ( $Profile )
	{
		$info.Environment['GOO_FRAME_PROFILE'] = '1'
		$info.RedirectStandardOutput = $true
	}

	$lock = Get-GooLock $folder
	$run = [pscustomobject]@{
		Name = "run $script:RunCount ($Layout)"; Folder = $folder; Trace = $trace; Guard = Get-Guard; Job = [BenchJob]::new(); Process = $null; Window = 0; Output = $null
		Lock = $lock; HadLock = Test-Path -LiteralPath $lock
	}
	try
	{
		$run.Process = [Diagnostics.Process]::Start( $info )
		$run.Job.Add( $run.Process )
		# Read to the end from the start: Goo's profile fills the pipe in seconds, and a full pipe blocks Loom's UI thread in Console.WriteLine.
		$run.Output = if ( $Profile ) { $run.Process.StandardOutput.ReadToEndAsync() } else { $null }
		$deadline = [DateTime]::UtcNow.AddSeconds( 90 )
		while ( -not ( ( Test-Path $trace ) -and ( Select-String -Path $trace -Pattern ' interactive$' -Quiet ) ) )
		{
			if ( $run.Process.HasExited -or [DateTime]::UtcNow -gt $deadline )
			{
				throw "Loom did not start for $Layout (exit $( if ( $run.Process.HasExited ) { $run.Process.ExitCode } else { 'none' } ))."
			}
			Start-Sleep -Milliseconds 100
		}

		while ( $run.Process.MainWindowHandle -eq 0 )
		{
			if ( $run.Process.HasExited -or [DateTime]::UtcNow -gt $deadline )
			{
				throw "Loom showed no window for $Layout."
			}
			Start-Sleep -Milliseconds 100
			$run.Process.Refresh()
		}

		$run.Window = $run.Process.MainWindowHandle
		[BenchWindow]::Place( $run.Window, 80, 60, 1400, 900 )
		return $run
	}
	catch
	{
		$failure = $_
		Stop-Loom $run
		throw $failure
	}
}

# Lists the Loom.exe processes of a launch's job: the window and any daemon it started.
function Get-JobLooms( $Run )
{
	$ids = @( $Run.Job.ListProcessIds() )
	if ( -not $ids.Count )
	{
		return @()
	}

	return @( Get-CimInstance Win32_Process -Filter "Name = 'Loom.exe'" | Where-Object { $_.ProcessId -in $ids } )
}

# Closes Loom normally, so it saves and stops its agents, ends whatever its job still holds (a bare daemon, the fixture agent, WebView2), removes Goo's
# lock file for the bare app ID, and checks the user's Loom against what Start-Loom recorded.
function Stop-Loom( $Run )
{
	[BenchWindow]::CloseCover()
	$breaches = @()
	$loomIds = @( if ( $Run.Process ) { $Run.Process.Id } )
	try
	{
		# Each Loom.exe it started must be bare on this launch's folder: a window, or a daemon that window started.
		$quoted = [regex]::Escape( $Run.Folder )
		foreach ( $loom in Get-JobLooms $Run )
		{
			$loomIds += [int]$loom.ProcessId
			if ( $loom.CommandLine -notmatch "--bare\s+`"?$quoted`"?(\s|$)" )
			{
				$breaches += "process $( $loom.ProcessId ) ran without its bare folder: $( $loom.CommandLine )"
			}
		}

		if ( $Run.Process -and -not $Run.Process.HasExited )
		{
			$Run.Process.CloseMainWindow() | Out-Null
			$Run.Process.WaitForExit( 15000 ) | Out-Null
		}

		$left = @( $Run.Job.ListProcessIds() )
		if ( $left.Count )
		{
			Write-Host "$( $Run.Name ): ending $( $left.Count ) processes it started and left running: $( $left -join ', ' )"
		}
	}
	finally
	{
		$Run.Job.Terminate()
		$deadline = [DateTime]::UtcNow.AddSeconds( 10 )
		while ( @( $Run.Job.ListProcessIds() ).Count -and [DateTime]::UtcNow -lt $deadline )
		{
			Start-Sleep -Milliseconds 100
		}
		$Run.Job.Dispose()
		# Only when the process never joined the job: it and its children are still the ones this launch started.
		if ( $Run.Process -and -not $Run.Process.HasExited )
		{
			$Run.Process.Kill( $true )
		}
		if ( -not $Run.HadLock -and ( Test-Path -LiteralPath $Run.Lock ) )
		{
			Remove-Item -LiteralPath $Run.Lock -Force -ErrorAction Ignore
		}
	}

	Assert-Guard $Run.Guard $Run.Name $breaches ( $loomIds | Sort-Object -Unique )
}

# Reads the trace: each line is milliseconds since the process started and an event.
function Read-Trace( $Run )
{
	Get-Content $Run.Trace | ForEach-Object {
		$space = $_.IndexOf( ' ' )
		[pscustomobject]@{ At = [double]$_.Substring( 0, $space ); Event = $_.Substring( $space + 1 ) }
	}
}

# Gets milliseconds since the process started, on the trace's clock.
function Get-Elapsed( $Run )
{
	( [DateTime]::now - $Run.Process.StartTime ).TotalMilliseconds
}

# Measures CPU as a share of one core and frames presented per second over -Seconds.
function Measure-Idle( $Run, [string] $Name )
{
	$Run.Process.Refresh()
	$cpuStart = $Run.Process.TotalProcessorTime
	$start = Get-Elapsed $Run
	Start-Sleep -Seconds $Seconds
	$Run.Process.Refresh()
	$cpu = ( $Run.Process.TotalProcessorTime - $cpuStart ).TotalMilliseconds
	$end = Get-Elapsed $Run
	# Count lines are written once a second; take those wholly inside the window.
	$counts = @( Read-Trace $Run | Where-Object { $_.Event -like 'frames *' -and $_.At -gt $start + 1000 -and $_.At -le $end } )
	$fps = if ( $counts.Count ) { ( $counts | ForEach-Object { [int]$_.Event.Substring( 7 ) } | Measure-Object -Sum ).Sum / $counts.Count } else { 0 }
	$focus = ( Read-Trace $Run | Where-Object { $_.Event -in 'focused', 'unfocused' } | Select-Object -Last 1 ).Event
	$result = [pscustomobject]@{
		Scenario = $Name
		CpuPercent = [math]::Round( $cpu / ( $end - $start ) * 100, 1 )
		Fps = [math]::Round( $fps, 1 )
		Focus = $focus
		WorkingSetMB = [math]::Round( $Run.Process.WorkingSet64 / 1MB )
		PrivateMB = [math]::Round( $Run.Process.PrivateMemorySize64 / 1MB )
		Threads = $Run.Process.Threads.Count
	}
	Write-Host ( '{0,-10} cpu {1,6}%  fps {2,6}  {3}' -f $Name, $result.CpuPercent, $result.Fps, $focus )
	return $result
}

# Focuses Loom's window and waits for the trace to show it.
function Set-Focused( $Run )
{
	[BenchWindow]::CloseCover()
	[BenchWindow]::Focus( $Run.Window ) | Out-Null
	Start-Sleep -Seconds 2
}

# Measures a scenario that runs in its own launch, focused.
function Measure-Launch( [string] $Name, [string] $Layout, [string] $Turn = '' )
{
	$run = Start-Loom $Layout $Turn
	try
	{
		Set-Focused $run
		Start-Sleep -Seconds 3
		return Measure-Idle $run $Name
	}
	finally
	{
		Stop-Loom $run
	}
}

# ---------------------------------------------------------------------------------------------------------------------
# Scenarios

$Results = [ordered]@{ Label = $Label; Exe = $Exe; Machine = $env:COMPUTERNAME; Date = ( Get-Date ).ToString( 's' ); Scenarios = @() }
$script:RunCount = 0
$Template = Join-Path $Root 'template'
Write-Host "Preparing data in $Root..."
$Seed = New-Template $Template
$failed = $false

try
{
	foreach ( $name in $Scenario )
	{
		switch ( $name )
		{
			'start'
			{
				$firsts = @()
				$interactives = @()
				for ( $launch = 0; $launch -lt $Starts; $launch++ )
				{
					$run = Start-Loom 'workspace' ''
					Stop-Loom $run
					$trace = Read-Trace $run
					$firsts += ( $trace | Where-Object Event -eq 'first-frame' ).At
					$interactives += ( $trace | Where-Object Event -eq 'interactive' ).At
					$phases = ( $trace | Where-Object { $_.Event -notlike 'frames *' -and $_.Event -notlike 'state *' -and $_.Event -notlike 'step *' -and $_.Event -notlike 'jit *' -and $_.Event -notin 'focused', 'unfocused', 'visible', 'hidden' } | ForEach-Object { "$( $_.Event )=$( $_.At )" } ) -join ' '
					# Steps (Shell/PerfTrace.cs): how long each took on its thread, and the milliseconds that thread spent compiling methods in it.
					$steps = ( $trace | Where-Object { $_.Event -like 'step *' } | ForEach-Object { $words = $_.Event.Split( ' ' ); "$( $words[1] )=$( $words[2] )/jit $( $words[5] )" } ) -join ' '
					$compiled = ( $trace | Where-Object { $_.Event -like 'jit *' } | Select-Object -First 1 | ForEach-Object { $words = $_.Event.Split( ' ' ); "$( $words[1] ) methods compiled in $( $words[2] ) ms before the first frame" } )
					Write-Host "start $launch  $phases"
					if ( $steps )
					{
						Write-Host "        steps $steps; $compiled"
					}
				}

				$first = ( $firsts | Sort-Object )[[int]( $firsts.Count / 2 )]
				$interactive = ( $interactives | Sort-Object )[[int]( $interactives.Count / 2 )]
				Write-Host ( 'start      first frame {0} ms, interactive {1} ms (median of {2})' -f $first, $interactive, $Starts )
				$Results.Scenarios += [pscustomobject]@{ Scenario = 'start'; FirstFrameMs = $first; InteractiveMs = $interactive; Phases = $phases }
			}

			'home' { $Results.Scenarios += Measure-Launch 'home' 'home' }
			'chat' { $Results.Scenarios += Measure-Launch 'chat' 'chat' }
			'streaming' { $Results.Scenarios += Measure-Launch 'streaming' 'working' 'stream' }
			'settings' { $Results.Scenarios += Measure-Launch 'settings' 'settings' }

			'working'
			{
				# One launch measures the working chat focused, then unfocused, covered and minimized, as those scenarios ask.
				$run = Start-Loom 'working' 'busy'
				try
				{
					Set-Focused $run
					Start-Sleep -Seconds 3
					$Results.Scenarios += Measure-Idle $run 'working'
					$states = @( $Scenario | Where-Object { $_ -in 'unfocused', 'covered', 'minimized' } )
					foreach ( $state in $states )
					{
						$bounds = [BenchWindow]::GetBounds( $run.Window )
						switch ( $state )
						{
							'unfocused' { [BenchWindow]::ShowCover( $bounds[0] + $bounds[2] + 20, $bounds[1], 200, 120 ) | Out-Null }
							'covered' { [BenchWindow]::ShowCover( $bounds[0] - 20, $bounds[1] - 20, $bounds[2] + 40, $bounds[3] + 40 ) | Out-Null }
							'minimized' { [BenchWindow]::Minimize( $run.Window ) }
						}

						Start-Sleep -Seconds 3
						$Results.Scenarios += Measure-Idle $run $state
						[BenchWindow]::CloseCover()
						Set-Focused $run
					}
				}
				finally
				{
					Stop-Loom $run
				}
			}

			'scroll'
			{
				# Turns the wheel over the long chat's transcript as Goo's own input, so neither the pointer nor a window in front stops it: one notch up,
				# a flick of six notches up, then the same down. Each step's frames are measured for 900 ms: a smooth scroll presents a frame every display
				# interval until it settles, so the gaps between frames show whether it kept the display's rate.
				$at = 'wheel:700:450'
				$steps = if ( $ScrollSteps ) { @( $ScrollSteps | ForEach-Object { "${at}:$_" } ) } else { @( "${at}:1:1", "${at}:1:6", "${at}:-1:1", "${at}:-1:6" ) }
				$run = Start-Loom 'chat' '' -Profile:( -not $NoProfile ) -Drive ( '1200|' + ( $steps -join '|' ) )
				try
				{
					Set-Focused $run
					if ( $CpuTrace )
					{
						# Samples Loom's stacks while it scrolls, for dotnet-trace report <file> topN.
						$nettrace = Join-Path $Root 'scroll.nettrace'
						& dotnet-trace collect -p $run.Process.Id --profile dotnet-sampled-thread-time --duration ( [TimeSpan]::FromSeconds( $Seconds ).ToString( 'hh\:mm\:ss' ) ) -o $nettrace | Out-Null
						Write-Host "scroll     cpu trace: $nettrace"
						Start-Sleep -Seconds 4
					}
					else
					{
						Start-Sleep -Seconds ( $Seconds + 4 )
					}
				}
				finally
				{
					Stop-Loom $run
				}

				$labels = @{ "${at}:1:1" = 'up 1 notch'; "${at}:1:6" = 'up flick (6)'; "${at}:-1:1" = 'down 1 notch'; "${at}:-1:6" = 'down flick (6)' }
				# The first round warms the code up, so only the later ones count.
				$switches = @( Read-Trace $run | Where-Object { $_.Event -like 'switch wheel:*' } | Select-Object -Skip $steps.Count | ForEach-Object {
					$fields = $_.Event.Split( ' ' )
					$gaps = if ( $fields.Count -gt 13 -and $fields[13] ) { @( $fields[13].Split( ',' ) | Select-Object -Skip 1 | ForEach-Object { [double]$_ } ) } else { @() }
					$step = if ( $labels.ContainsKey( $fields[1] ) ) { $labels[$fields[1]] } else { $fields[1].Substring( $at.Length + 1 ) }
					[pscustomobject]@{ Step = $step; First = [double]$fields[5]; Frames = [int]$fields[7]; MaxGap = [double]$fields[9]; Gaps = $gaps }
				} )
				foreach ( $group in ( $switches | Group-Object Step ) )
				{
					$gaps = @( $group.Group | ForEach-Object { $_.Gaps } | Sort-Object )
					$row = [pscustomobject]@{
						Scenario = 'scroll'
						Step = $group.Name
						Count = $group.Count
						FirstFrameMs = [math]::Round( ( $group.Group.First | Measure-Object -Average ).Average, 1 )
						FramesPerStep = [math]::Round( ( $group.Group.Frames | Measure-Object -Average ).Average, 1 )
						GapP50Ms = if ( $gaps.Count ) { $gaps[[int]( ( $gaps.Count - 1 ) * 0.5 )] } else { $null }
						GapP95Ms = if ( $gaps.Count ) { $gaps[[int]( ( $gaps.Count - 1 ) * 0.95 )] } else { $null }
						MaxGapMs = ( $group.Group.MaxGap | Measure-Object -Maximum ).Maximum
					}
					Write-Host ( 'scroll     {0,-16} x{1}  first frame {2} ms  frames {3}/step  gap p50 {4} ms p95 {5} ms  worst {6} ms' -f $row.Step, $row.Count, $row.FirstFrameMs, $row.FramesPerStep, $row.GapP50Ms, $row.GapP95Ms, $row.MaxGapMs )
					$Results.Scenarios += $row
				}

				# Goo prints frame statistics every 240 frames after 60 warm-up frames.
				$lines = if ( $run.Output -and $run.Output.Wait( 5000 ) ) { $run.Output.Result -split "`r?`n" } else { @() }
				# The whole output, with Goo's per-phase frame statistics, stays beside the bench data for a closer look.
				$lines | Set-Content ( Join-Path $Root 'scroll-output.log' )
				Write-Host "scroll     output: $( Join-Path $Root 'scroll-output.log' )"
				$tails = @( $lines | Where-Object { $_ -like '*goo.profile.tail_ms/frame*phase=frame *' } )
				$p50 = @( $tails | ForEach-Object { [double]( [regex]::Match( $_, 'p50=([\d.]+)' ).Groups[1].Value ) } )
				$p99 = @( $tails | ForEach-Object { [double]( [regex]::Match( $_, 'p99=([\d.]+)' ).Groups[1].Value ) } )
				if ( $p50.Count )
				{
					Write-Host ( 'scroll     goo frame p50 {0} ms p99 {1} ms' -f ( $p50 | Measure-Object -Average ).Average, ( $p99 | Measure-Object -Maximum ).Maximum )
				}
				$Results.Scenarios += [pscustomobject]@{ Scenario = 'scroll-profile'; Profile = @( $lines | Where-Object { $_ -like '*goo.profile*' } | Select-Object -Last 12 ) }
			}

			'switch'
			{
				# Switches chats and profiles as a click would, in a loop: a short chat, the long one, a chat of another project, a very long one,
				# Personal, its chat, Work. Each step's first run is cold: its chat or profile had not been shown in this session yet.
				$ids = @( 1, 0, 4, 8, 10 ) | ForEach-Object { $Seed.Chats[$_].ToString( 'N' ) }
				$names = @{ $ids[0] = 'chat-short'; $ids[1] = 'chat-long'; $ids[2] = 'chat-other-project'; $ids[3] = 'chat-1400-messages'; $ids[4] = 'chat-personal' }
				$steps = @( $ids[0..3] | ForEach-Object { "lokn://conversations/open/$_" } ) + @( 'profile', "lokn://conversations/open/$( $ids[4] )", 'profile' )
				$run = Start-Loom 'chat' '' -Drive ( '1500|' + ( $steps -join '|' ) )
				try
				{
					Set-Focused $run
					Start-Sleep -Seconds $Seconds
					$index = 0
					$switches = @( Read-Trace $run | Where-Object { $_.Event -like 'switch *' } | ForEach-Object {
						$fields = $_.Event.Split( ' ' )
						# The two profile switches go to Personal, then back to Work.
						$step = if ( $fields[1] -eq 'profile' ) { if ( $index % $steps.Count -eq 4 ) { 'to-personal' } else { 'to-work' } } else { $names[$fields[1]] }
						$cold = $index -lt $steps.Count
						$index++
						[pscustomobject]@{
							Step = $step + $( if ( $cold ) { ' (cold)' } else { '' } ); Handler = [double]$fields[3]; First = [double]$fields[5]; Frames = [int]$fields[7]; MaxGap = [double]$fields[9]; Dropped = [int]$fields[11]
							Gaps = if ( $fields.Count -gt 13 ) { $fields[13] } else { '' }
						}
					} )
					$rows = foreach ( $group in ( $switches | Group-Object Step ) )
					{
						[pscustomobject]@{
							Scenario = 'switch'
							Step = $group.Name
							Count = $group.Count
							HandlerMs = [math]::Round( ( $group.Group.Handler | Measure-Object -Average ).Average, 1 )
							FirstFrameMs = [math]::Round( ( $group.Group.First | Measure-Object -Average ).Average, 1 )
							MaxGapMs = [math]::Round( ( $group.Group.MaxGap | Measure-Object -Maximum ).Maximum, 1 )
							DroppedPerSwitch = [math]::Round( ( $group.Group.Dropped | Measure-Object -Average ).Average, 1 )
							Gaps = @( $group.Group.Gaps )
						}
					}
					foreach ( $row in $rows )
					{
						Write-Host ( 'switch     {0,-20} x{1}  handler {2} ms  first frame {3} ms  worst gap {4} ms  dropped {5}/switch' -f $row.Step, $row.Count, $row.HandlerMs, $row.FirstFrameMs, $row.MaxGapMs, $row.DroppedPerSwitch )
						$Results.Scenarios += $row
					}
				}
				finally
				{
					Stop-Loom $run
				}
			}

			'memory'
			{
				$run = Start-Loom 'workspace' ''
				try
				{
					Start-Sleep -Seconds 10
					$run.Process.Refresh()
					$startSet = [math]::Round( $run.Process.WorkingSet64 / 1MB )
					$startPrivate = [math]::Round( $run.Process.PrivateMemorySize64 / 1MB )
					$cpuStart = $run.Process.TotalProcessorTime
					Start-Sleep -Seconds ( $MemoryMinutes * 60 )
					$run.Process.Refresh()
					$cpu = ( $run.Process.TotalProcessorTime - $cpuStart ).TotalMilliseconds
					$result = [pscustomobject]@{
						Scenario = 'memory'
						StartWorkingSetMB = $startSet
						StartPrivateMB = $startPrivate
						LaterWorkingSetMB = [math]::Round( $run.Process.WorkingSet64 / 1MB )
						LaterPrivateMB = [math]::Round( $run.Process.PrivateMemorySize64 / 1MB )
						Threads = $run.Process.Threads.Count
						IdleCpuPercent = [math]::Round( $cpu / ( $MemoryMinutes * 60000 ) * 100, 1 )
					}
					Write-Host ( 'memory     start {0} MB ws / {1} MB private; after {2} min {3} MB ws / {4} MB private; {5} threads; cpu {6}%' -f $startSet, $startPrivate, $MemoryMinutes, $result.LaterWorkingSetMB, $result.LaterPrivateMB, $result.Threads, $result.IdleCpuPercent )
					$Results.Scenarios += $result
				}
				finally
				{
					Stop-Loom $run
				}
			}

			{ $_ -in 'unfocused', 'covered', 'minimized' }
			{
				if ( $Scenario -notcontains 'working' )
				{
					Write-Host "$name runs inside the working scenario; add -Scenario working."
				}
			}
		}
	}
}
catch
{
	Write-Host "Error: $_"
	$failed = $true
}

if ( $Out )
{
	[IO.File]::WriteAllText( $Out, ( [pscustomobject]$Results | ConvertTo-Json -Depth 8 ), [Text.UTF8Encoding]::new( $false ) )
	Write-Host "Results: $Out"
}

if ( $failed )
{
	Write-Host 'BENCH FAILED'
	exit 1
}

Write-Host 'BENCH OK'
