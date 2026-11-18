<#
.SYNOPSIS
Builds and starts Lokn, optionally closing a running instance first.

.DESCRIPTION
Starts the app detached, with the repository as its working directory, and prints
its process ID. When Lokn is already running from this checkout the script stops
with a message unless -Restart is given.

-Restart asks the running window to close normally, so Lokn saves its layout,
conversations, and pending settings, then builds and starts the new version. It
never kills the process: if Lokn does not exit in time, it is left running.

The restart runs in a separate detached process and this script returns at once,
because an agent working inside a Lokn chat is a child of Lokn and is stopped when
Lokn closes. Read the log file the script prints to see how the restart went.

Closing the app interrupts whatever the user is doing in it. Only pass -Restart
when the user asked for a restart.

.EXAMPLE
pwsh scripts/run.ps1

.EXAMPLE
pwsh scripts/run.ps1 -Restart
#>
param(
	# Closes a running Lokn from this checkout before building and starting.
	[switch] $Restart,

	# Starts the existing binaries without building.
	[switch] $NoBuild,

	# Opens the Omp setup panel at startup.
	[switch] $OmpSetup,

	# Release or Debug.
	[ValidateSet( 'Release', 'Debug' )]
	[string] $Configuration = 'Release',

	# Seconds to wait for a running Lokn to close.
	[int] $CloseTimeoutSeconds = 20,

	# Internal: set on the detached process that performs a restart.
	[switch] $Detached
)

. "$PSScriptRoot\common.ps1"

$logFile = Join-Path ( [IO.Path]::GetTempPath() ) 'Lokn\restart.log'
$running = @( Get-RunningLokn )

if ( $running.Count -gt 0 -and -not $Restart )
{
	Write-Host "Lokn is already running (PID $( $running.Id -join ', ' )). Pass -Restart to close it and start the new build."
	exit 1
}

if ( $running.Count -gt 0 -and -not $Detached )
{
	# A process created through CIM is not a descendant of this one, so it survives Lokn stopping its agents.
	$arguments = @( '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Detached', '-Configuration', $Configuration, '-CloseTimeoutSeconds', $CloseTimeoutSeconds )
	if ( $Restart )
	{
		$arguments += '-Restart'
	}

	if ( $NoBuild )
	{
		$arguments += '-NoBuild'
	}

	if ( $OmpSetup )
	{
		$arguments += '-OmpSetup'
	}

	$shell = ( Get-Process -Id $PID ).Path
	$parameters = @{
		CommandLine = "`"$shell`" $( $arguments -join ' ' )"
		CurrentDirectory = $RepoRoot
	}
	$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments $parameters
	if ( $created.ReturnValue -ne 0 )
	{
		Write-Host "Could not start the detached restart (Win32_Process.Create returned $( $created.ReturnValue ))."
		exit 1
	}

	Write-Host "RESTARTING Lokn in a detached process (PID $( $created.ProcessId )). Progress and errors: $logFile"
	exit 0
}

if ( $Detached )
{
	New-Item -ItemType Directory -Force ( Split-Path -Parent $logFile ) | Out-Null
	Start-Transcript -Path $logFile -Force | Out-Null
}

foreach ( $process in $running )
{
	Write-Host "Closing Lokn (PID $( $process.Id ))..."
	[void] $process.CloseMainWindow()
	if ( -not $process.WaitForExit( $CloseTimeoutSeconds * 1000 ) )
	{
		Write-Host "Lokn (PID $( $process.Id )) did not close within $CloseTimeoutSeconds seconds. It was left running; close it by hand and retry."
		exit 1
	}
}

if ( -not $NoBuild )
{
	& "$PSScriptRoot\build.ps1" -Configuration $Configuration
	if ( $LASTEXITCODE -ne 0 )
	{
		exit $LASTEXITCODE
	}
}

$executable = "$RepoRoot\bin\$Configuration\net10.0\Loom.exe"
if ( -not ( Test-Path $executable ) )
{
	Write-Host "Not built: $executable"
	exit 1
}

$start = @{
	FilePath = $executable
	WorkingDirectory = $RepoRoot
	PassThru = $true
}
if ( $OmpSetup )
{
	$start.ArgumentList = '--omp-setup'
}

$started = Start-Process @start
Write-Host "STARTED Lokn ($Configuration), PID $( $started.Id )"
