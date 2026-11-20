<#
.SYNOPSIS
Builds one feature module and reloads it in the running Lokn.

.DESCRIPTION
Builds the module's project in the configuration the running Lokn uses. The build
deploys the DLL beside the application, where Lokn notices it and swaps the module
in without restarting. The script then waits for Lokn's outcome and prints it:

  RELOADED  the new code is live; open panels were reopened in place
  FAILED    the module did not load; the previous version keeps running
  NO CHANGE the build produced the same DLL, so nothing was reloaded

A daemon started from the same build (Loom.exe --host, docs/remote.md) reloads the
module too, but only once none of its chats' agents works. The script prints its
outcome from the daemon's host.log as well:

  RELOADED  the daemon runs the new code
  DEFERRED  a chat is working; the daemon reloads once none is, and logs RELOADED or FAILED then
  FAILED    the module did not load in the daemon; the previous version keeps running there

Exits non-zero when the module does not build or does not load in Lokn or the daemon.

Modules that depend on the rebuilt one reload with it: rebuilding Omp also reloads
Conversations and Worktrees. Reloading Omp or Conversations closes and reopens the
chat panels, which stops their running agent turns; conversations resume from their
saved sessions. An agent working inside a Lokn chat is stopped by such a reload
before this script can print the outcome, so read the log file afterwards. In the
daemon such a reload waits for the turns to end instead.

Only code inside the module is reloaded. A change to Core, the shell or Program.cs
needs scripts/run.ps1 -Restart.

.EXAMPLE
pwsh scripts/reload-module.ps1 -Name Worktrees
#>
param(
	# The module folder under Modules: Omp, Conversations, Worktrees, Terminal, Browser. Chat builds with Conversations.
	[Parameter( Mandatory )]
	[string] $Name,

	# Seconds to wait for the running Lokn to report the reload.
	[int] $TimeoutSeconds = 30
)

. "$PSScriptRoot\common.ps1"

# The chat panel is part of the Conversations module's assembly.
$projectName = $Name
if ( $Name -eq 'Chat' )
{
	$projectName = 'Conversations'
}

$project = Get-ChildItem "$RepoRoot\Modules" -Recurse -Filter 'Lokn.Modules.*.csproj' |
	Where-Object { $_.BaseName -eq "Lokn.Modules.$projectName" } |
	Select-Object -First 1
if ( -not $project )
{
	$known = Get-ChildItem "$RepoRoot\Modules" -Recurse -Filter 'Lokn.Modules.*.csproj' | ForEach-Object { $_.BaseName -replace '^Lokn\.Modules\.', '' }
	Write-Host "No module project named '$Name'. Known modules: $( ( $known | Sort-Object ) -join ', ' )."
	exit 1
}

# Lokn only watches the modules folder of the build it was started from; so does a daemon started from it.
$running = @( Get-RunningLokn )
$configuration = @( Get-LockedConfiguration ) | Select-Object -First 1
if ( -not $configuration )
{
	$configuration = 'Release'
}

$assembly = "$RepoRoot\bin\$configuration\net10.0\modules\$( $project.BaseName ).dll"
$temp = Join-Path ( [IO.Path]::GetTempPath() ) 'Lokn'

function Get-Stamp( [string] $Path )
{
	if ( Test-Path $Path )
	{
		$item = Get-Item $Path
		return "$( $item.LastWriteTimeUtc.Ticks ):$( $item.Length )"
	}

	return ''
}

function Get-LogLineCount( [string] $Path )
{
	if ( Test-Path $Path )
	{
		return @( Get-Content $Path ).Count
	}

	return 0
}

# A window logs reloads to modules.log; a daemon (--host) to host.log in %TEMP%\Lokn, or in its --bare folder.
$watches = @( $running | ForEach-Object {
	$commandLine = "$( ( Get-CimInstance Win32_Process -Filter "ProcessId = $( $_.Id )" ).CommandLine )"
	if ( $commandLine -notmatch '(^|\s)--host(\s|$)' )
	{
		[pscustomobject]@{ Name = 'Lokn'; Log = Join-Path $temp 'modules.log' }
	}
	elseif ( $commandLine -match '(^|\s)--bare\s+(?:"([^"]*)"|(\S+))' )
	{
		$bare = "$( $Matches[2] )$( $Matches[3] )".TrimEnd( '\' )
		[pscustomobject]@{ Name = "Daemon ($bare)"; Log = Join-Path $bare 'host.log' }
	}
	else
	{
		[pscustomobject]@{ Name = 'Daemon'; Log = Join-Path $temp 'host.log' }
	}
} | Sort-Object Log -Unique | ForEach-Object {
	$_ | Add-Member Seen ( Get-LogLineCount $_.Log ) -PassThru | Add-Member Outcome $null -PassThru
} )

$before = Get-Stamp $assembly

Write-Host "Building $( $project.BaseName ) ($configuration)..."
dotnet build $project.FullName -c $configuration -nologo -v q
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host "MODULE DOES NOT BUILD: $Name"
	exit $LASTEXITCODE
}

if ( -not ( Test-Path $assembly ) )
{
	Write-Host "BUILT $Name ($configuration). This module is not deployed to the application, so there is nothing to reload."
	exit 0
}

if ( ( Get-Stamp $assembly ) -eq $before )
{
	Write-Host "NO CHANGE: the build produced the same DLL, so nothing was reloaded."
	exit 0
}

if ( $watches.Count -eq 0 )
{
	Write-Host "BUILT $Name ($configuration). Lokn is not running; it loads the new build at its next start."
	exit 0
}

# A window reports RELOADED or FAILED; a daemon may first report DEFERRED, which is its outcome for now.
$clock = [Diagnostics.Stopwatch]::StartNew()
while ( $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds -and @( $watches | Where-Object { -not $_.Outcome } ).Count -gt 0 )
{
	foreach ( $watch in $watches | Where-Object { -not $_.Outcome } )
	{
		if ( Test-Path $watch.Log )
		{
			$watch.Outcome = Get-Content $watch.Log | Select-Object -Skip $watch.Seen |
				Where-Object { $_ -match [regex]::Escape( $project.BaseName ) -and $_ -match ' (RELOADED|FAILED|DEFERRED) ' } |
				Select-Object -Last 1
		}
	}

	Start-Sleep -Milliseconds 250
}

$exitCode = 0
foreach ( $watch in $watches )
{
	$isDaemon = $watch.Name -ne 'Lokn'
	if ( $watch.Outcome )
	{
		$prefix = ''
		if ( $isDaemon )
		{
			$prefix = "$( $watch.Name ): "
		}
		Write-Host "$prefix$( $watch.Outcome )"
		if ( $watch.Outcome -match ' FAILED ' )
		{
			$exitCode = 1
		}
		elseif ( $watch.Outcome -match ' DEFERRED ' )
		{
			Write-Host "$( $watch.Name ) reloads the module once no chat works, then logs RELOADED or FAILED in $( $watch.Log )."
		}
	}
	elseif ( $isDaemon )
	{
		Write-Host "$( $watch.Name ) reported no outcome within $TimeoutSeconds seconds; it reloads when idle. See $( $watch.Log )."
	}
	else
	{
		Write-Host "Lokn reported no outcome within $TimeoutSeconds seconds. See $( $watch.Log )."
		$exitCode = 1
	}
}

exit $exitCode
