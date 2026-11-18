# Shared helpers for the Lokn scripts. Dot-source this file; do not run it directly.

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Returns the Lokn processes that were started from this checkout's build output.
# Builds from before the rename to Loom still run as Lokn.exe, and must be found so a restart closes them first.
function Get-RunningLokn
{
	Get-Process Loom, Lokn -ErrorAction Ignore | Where-Object {
		$_.Path -and $_.Path.StartsWith( $RepoRoot, [StringComparison]::OrdinalIgnoreCase )
	}
}

# Returns the build configurations whose output a running Lokn has locked.
function Get-LockedConfiguration
{
	# bin\<Configuration>\net10.0\Loom.exe
	Get-RunningLokn | ForEach-Object { Split-Path -Leaf ( Split-Path -Parent ( Split-Path -Parent $_.Path ) ) }
}

# Picks a configuration that can be built: the requested one, or whichever a running Lokn is not using.
function Resolve-Configuration( [string] $Requested )
{
	$locked = @( Get-LockedConfiguration )
	if ( $Requested )
	{
		if ( $locked -contains $Requested )
		{
			throw "Lokn is running from the $Requested build, so it cannot be rebuilt. Omit -Configuration, or run scripts/run.ps1 -Restart."
		}

		return $Requested
	}

	if ( $locked -contains 'Release' )
	{
		return 'Debug'
	}

	return 'Release'
}
