<#
.SYNOPSIS
Builds Lokn and its checks.

.DESCRIPTION
Builds the test project, which also builds the app and Core. Warnings are errors.
Without -Configuration it builds Release, or Debug when a running Lokn has the
Release output locked. Exits non-zero when the build fails.

.EXAMPLE
pwsh scripts/build.ps1
#>
param(
	# Release or Debug. Defaults to whichever a running Lokn is not using.
	[ValidateSet( 'Release', 'Debug' )]
	[string] $Configuration
)

. "$PSScriptRoot\common.ps1"

$Configuration = Resolve-Configuration $Configuration
Write-Host "Building Lokn ($Configuration)..."
dotnet build "$RepoRoot\tests\Lokn.Tests" -c $Configuration -nologo -v q
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host "BUILD FAILED ($Configuration)"
	exit $LASTEXITCODE
}

Write-Host "BUILD OK ($Configuration)"
