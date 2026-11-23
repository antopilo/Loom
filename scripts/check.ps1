<#
.SYNOPSIS
Builds Lokn and runs its check suites.

.DESCRIPTION
Runs one or more suites of the Lokn.Tests executable and prints PASS or FAIL per
suite. Exits non-zero when the build or any suite fails.

Headless suites (no window):
  default        core, hotload, updates, community, and settings checks
  features       layout, conversations, omp, chat, worktrees, terminal, browser, design, git, and remote
  layout, conversations, omp, chat, worktrees, terminal, browser, design, git, remote, hotload, updates, community, settings
  all            default and features

Suites that open a real window on the desktop for a few seconds:
  chat-view, terminal-view, omp-setup, startup
  -Ui adds the window checks to default, settings, or features.

Only run window suites when the change affects rendering or input, and say so first:
they take focus on the user's desktop.

.EXAMPLE
pwsh scripts/check.ps1

.EXAMPLE
pwsh scripts/check.ps1 -Suite worktrees,hotload

.EXAMPLE
pwsh scripts/check.ps1 -Suite settings -Ui
#>
[CmdletBinding( PositionalBinding = $false )]
param(
	# The suites to run, in order, separated by commas or spaces. Defaults to 'default'.
	[string[]] $Suite = @(),

	# Further suite names given without -Suite, or after a comma followed by a space.
	[Parameter( ValueFromRemainingArguments )]
	[string[]] $MoreSuites = @(),

	# Also runs the checks that open a real window.
	[switch] $Ui,

	# Release or Debug. Defaults to whichever a running Lokn is not using.
	[ValidateSet( 'Release', 'Debug' )]
	[string] $Configuration,

	# Skips the build and runs the existing binaries.
	[switch] $NoBuild
)

. "$PSScriptRoot\common.ps1"

# The check executable takes one selecting flag per run; 'default' takes none.
$flags = @{
	'default' = @()
	'features' = @( '--features-only' )
	'layout' = @( '--layout-only' )
	'conversations' = @( '--conversations-only' )
	'omp' = @( '--omp-only' )
	'chat' = @( '--chat-only' )
	'worktrees' = @( '--worktrees-only' )
	'terminal' = @( '--terminal-only' )
	'browser' = @( '--browser-only' )
	'design' = @( '--design-only' )
	'git' = @( '--git-only' )
	'remote' = @( '--remote-only' )
	'hotload' = @( '--hotload-only' )
	'updates' = @( '--updates-only' )
	'community' = @( '--community-only' )
	'settings' = @( '--settings-only' )
	'chat-view' = @( '--chat-view-only' )
	'terminal-view' = @( '--terminal-view-only' )
	'omp-setup' = @( '--omp-setup-only' )
	'startup' = @( '--startup-only' )
}

# 'pwsh -File' passes "a, b" as separate words with commas attached, so split the names here.
$requested = @( $Suite ) + @( $MoreSuites )
if ( $requested.Count -eq 0 )
{
	$requested = @( 'default' )
}

$suites = $requested |
	ForEach-Object { $_ -split '[,\s]+' } |
	Where-Object { $_ } |
	ForEach-Object { if ( $_ -eq 'all' ) { 'default'; 'features' } else { $_ } } |
	Select-Object -Unique
$unknown = @( $suites | Where-Object { -not $flags.ContainsKey( $_ ) } )
if ( $unknown.Count -gt 0 )
{
	Write-Host "Unknown suite: $( $unknown -join ', ' ). Known suites: all, $( ( $flags.Keys | Sort-Object ) -join ', ' )."
	exit 1
}

$Configuration = Resolve-Configuration $Configuration
if ( -not $NoBuild )
{
	& "$PSScriptRoot\build.ps1" -Configuration $Configuration
	if ( $LASTEXITCODE -ne 0 )
	{
		exit $LASTEXITCODE
	}
}

$failed = @()
foreach ( $name in $suites )
{
	$arguments = @( $flags[$name] )
	if ( $Ui )
	{
		$arguments += '--ui'
	}

	Write-Host ""
	Write-Host "== $name"
	dotnet run --project "$RepoRoot\tests\Lokn.Tests" -c $Configuration --no-build -- @arguments
	if ( $LASTEXITCODE -ne 0 )
	{
		$failed += $name
	}
}

Write-Host ""
foreach ( $name in $suites )
{
	if ( $failed -contains $name )
	{
		Write-Host "FAIL $name"
	}
	else
	{
		Write-Host "PASS $name"
	}
}

if ( $failed.Count -gt 0 )
{
	exit 1
}
