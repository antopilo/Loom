<#
.SYNOPSIS
Builds the Loom 0.2.0 data loader that the downgrade check runs.

.DESCRIPTION
Extracts the 0.2.0 sources (git archive of a ref, 'main' by default) into %TEMP%\loom-020, adds the
fixture driver from tests\Downgrade-0.2.0 to its test project and builds it in Debug, all under
%TEMP%\loom-020. The upgrade checks (suite 'updates') then open the data 0.3.0 saved with it:

  dotnet %TEMP%\loom-020\tests\Lokn.Tests\bin\Debug\net10.0\Lokn.Tests.dll --load-data <folder>

The same driver writes a new fixture with --make-fixture <empty folder>. Run either on a copy:
0.2.0 writes to the folder it opens. Prints LOADER OK or LOADER FAILED.

.EXAMPLE
pwsh scripts/loader-020.ps1
#>
param(
	# The git ref holding the 0.2.0 sources.
	[string] $Ref = 'main'
)

. "$PSScriptRoot\common.ps1"

$target = Join-Path ( [IO.Path]::GetTempPath() ) 'loom-020'
if ( Test-Path $target )
{
	Remove-Item $target -Recurse -Force
}
New-Item -ItemType Directory $target | Out-Null

$archive = Join-Path ( [IO.Path]::GetTempPath() ) 'loom-020.tar'
git -C $RepoRoot archive --format=tar -o $archive $Ref
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host "LOADER FAILED: git archive $Ref"
	exit 1
}
tar -xf $archive -C $target
Remove-Item $archive

# The driver goes into the 0.2.0 test project, with the flags that run it ahead of every suite.
$tests = Join-Path $target 'tests\Lokn.Tests'
Copy-Item ( Join-Path $RepoRoot 'tests\Downgrade-0.2.0\FixtureDriver.cs' ) $tests
$program = Join-Path $tests 'Program.cs'
$lines = [Collections.Generic.List[string]]( Get-Content $program )
$branch = @(
	''
	'// --make-fixture <folder> writes a data folder with this version''s stores; --load-data <folder> loads one and prints what it read.'
	'if ( args is ["--make-fixture" or "--load-data", var dataFolder] )'
	'{'
	"`ttry"
	"`t{"
	"`t`tEnvironment.ExitCode = args[0] == ""--make-fixture"" ? FixtureDriver.MakeFixture( dataFolder ) : FixtureDriver.LoadData( dataFolder );"
	"`t}"
	"`tcatch ( Exception error )"
	"`t{"
	"`t`tConsole.WriteLine( `$""EXCEPTION {error}"" );"
	"`t`tEnvironment.ExitCode = 2;"
	"`t}"
	"`treturn;"
	'}'
)
$lines.InsertRange( 1, [string[]]$branch )
Set-Content $program $lines

dotnet build $tests -c Debug -nologo -v q
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host 'LOADER FAILED: the 0.2.0 build did not compile'
	exit $LASTEXITCODE
}

Write-Host "LOADER OK: $( Join-Path $tests 'bin\Debug\net10.0\Lokn.Tests.dll' )"
