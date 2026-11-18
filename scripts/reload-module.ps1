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

Exits non-zero when the module does not build or does not load.

Modules that depend on the rebuilt one reload with it: rebuilding Omp also reloads
Conversations and Worktrees. Reloading Omp or Conversations closes and reopens the
chat panels, which stops their running agent turns; conversations resume from their
saved sessions. An agent working inside a Lokn chat is stopped by such a reload
before this script can print the outcome, so read the log file afterwards.

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

# Lokn only watches the modules folder of the build it was started from.
$running = @( Get-RunningLokn )
$configuration = @( Get-LockedConfiguration ) | Select-Object -First 1
if ( -not $configuration )
{
	$configuration = 'Release'
}

$assembly = "$RepoRoot\bin\$configuration\net10.0\modules\$( $project.BaseName ).dll"
$log = Join-Path ( [IO.Path]::GetTempPath() ) 'Lokn\modules.log'

function Get-Stamp( [string] $Path )
{
	if ( Test-Path $Path )
	{
		$item = Get-Item $Path
		return "$( $item.LastWriteTimeUtc.Ticks ):$( $item.Length )"
	}

	return ''
}

function Get-LogLineCount
{
	if ( Test-Path $log )
	{
		return @( Get-Content $log ).Count
	}

	return 0
}

$before = Get-Stamp $assembly
$seen = Get-LogLineCount

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

if ( $running.Count -eq 0 )
{
	Write-Host "BUILT $Name ($configuration). Lokn is not running; it loads the new build at its next start."
	exit 0
}

$clock = [Diagnostics.Stopwatch]::StartNew()
while ( $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds )
{
	$lines = @()
	if ( Test-Path $log )
	{
		$lines = @( Get-Content $log | Select-Object -Skip $seen | Where-Object { $_ -match [regex]::Escape( $project.BaseName ) } )
	}

	$outcome = $lines | Where-Object { $_ -match ' (RELOADED|FAILED) ' } | Select-Object -Last 1
	if ( $outcome )
	{
		Write-Host $outcome
		if ( $outcome -match ' FAILED ' )
		{
			exit 1
		}

		exit 0
	}

	Start-Sleep -Milliseconds 250
}

Write-Host "Lokn reported no outcome within $TimeoutSeconds seconds. See $log."
exit 1
