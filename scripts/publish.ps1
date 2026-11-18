<#
.SYNOPSIS
Publishes Loom as a self-contained folder that runs on a PC without .NET.

.DESCRIPTION
Publishes Loom for win-x64 with the .NET runtime included, and every built-in
module in the modules folder beside Loom.exe. The output is a folder, not one
file: the source-module compiler reads the framework assemblies from disk, and
the modules are found by reflection, so single-file and trimming would break them.

The folder is cleared first, so it holds only this build. Exits non-zero when
the publish fails or a module is missing from the output.

.EXAMPLE
pwsh scripts/publish.ps1
pwsh scripts/publish.ps1 -Output D:\Builds\Loom
#>
param(
	# The folder to publish into. Defaults to artifacts\publish\win-x64 in the repository.
	[string] $Output,
	# The release version, such as 0.2.0. Defaults to the Version in Lokn.csproj.
	[string] $Version
)

. "$PSScriptRoot\common.ps1"

if ( -not $Output )
{
	$Output = "$RepoRoot\artifacts\publish\win-x64"
}

$Output = [IO.Path]::GetFullPath( $Output )
if ( Test-Path $Output )
{
	Remove-Item $Output -Recurse -Force
}

# Global properties flow to the module projects, so their deploy step copies each DLL into the published modules folder.
$modules = Join-Path $Output 'modules\'
Write-Host "Publishing Loom to $Output..."
dotnet publish "$RepoRoot\Lokn.csproj" -c Release -r win-x64 --self-contained -nologo -v q `
	-o $Output `
	-p:PublishSingleFile=false `
	-p:PublishTrimmed=false `
	-p:SatelliteResourceLanguages=en `
	"-p:LoknModulesDirectory=$modules" `
	$( if ( $Version ) { "-p:Version=$Version" } )
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host 'PUBLISH FAILED'
	exit $LASTEXITCODE
}

# The shipped modules are the ones Lokn.csproj builds; test-only modules such as Welcome are not among them.
$expected = ( [xml]( Get-Content "$RepoRoot\Lokn.csproj" -Raw ) ).Project.ItemGroup.ProjectReference |
	Where-Object { $_.ReferenceOutputAssembly -eq 'false' } |
	ForEach-Object { "$( [IO.Path]::GetFileNameWithoutExtension( $_.Include ) ).dll" }
$missing = @( $expected | Where-Object { -not ( Test-Path ( Join-Path $modules $_ ) ) } )
if ( $missing.Count -gt 0 )
{
	Write-Host "PUBLISH FAILED: modules missing from the output: $( $missing -join ', ' )"
	exit 1
}

$size = ( Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum ).Sum / 1MB
Write-Host ( 'PUBLISH OK ({0} modules, {1:N0} MB): {2}' -f $expected.Count, $size, $Output )
