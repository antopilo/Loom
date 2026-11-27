<#
.SYNOPSIS
Compiles Loom's Goo shader effects from their .slang source into the committed .goo-effect programs.

.DESCRIPTION
Builds need no Slang toolchain: the compiled programs are committed beside their source and embedded. Run this after changing a .slang file,
or after a Goo update that changes the effect program format, and commit the result.

Goo's effect compiler (in the Goo package's tools) needs the exact Slang release it pins, found through SLANG_SDK or PATH, and spirv-val from
the Vulkan SDK it pins, found through VULKAN_SDK or PATH; it names both versions when one is missing or different.

.EXAMPLE
$env:SLANG_SDK = 'C:\slang-2026.16'; pwsh scripts/shaders.ps1
#>
param()

. "$PSScriptRoot\common.ps1"

# Each effect and whether it is source-independent: it draws by itself instead of changing the pixels of the element it is set on.
$effects = @(
	@{ Source = 'Modules\Conversations\Chat\Shaders\shimmer.slang'; SourceIndependent = 'false' }
	@{ Source = 'Modules\Conversations\Chat\Shaders\glow.slang'; SourceIndependent = 'true' }
)

$root = Split-Path -Parent $PSScriptRoot
$version = ( [xml]( Get-Content ( Join-Path $root 'Core\Lokn.Core.csproj' ) ) ).Project.ItemGroup.PackageReference | Where-Object Include -eq 'Goo' | Select-Object -First 1 -ExpandProperty Version
$goo = Join-Path $env:USERPROFILE ".nuget\packages\goo\$version"
$tool = Join-Path $goo 'tools\net10.0\any\Goo.ShaderEffectTool.dll'
$authoring = Join-Path $goo 'contentFiles\any\any\Vulkan\Shaders\Authoring'
if ( -not ( Test-Path $tool ) )
{
	Write-Host "SHADERS FAILED: Goo $version's effect compiler is missing at $tool; build once to restore the package."
	exit 1
}

foreach ( $effect in $effects )
{
	$source = Join-Path $root $effect.Source
	$program = [IO.Path]::ChangeExtension( $source, '.goo-effect' )
	$manifest = Join-Path ( [IO.Path]::GetTempPath() ) ( [IO.Path]::GetFileName( $program ) + '.json' )
	& dotnet exec $tool compile $source $authoring $program $manifest $effect.SourceIndependent
	if ( $LASTEXITCODE -ne 0 )
	{
		Write-Host "SHADERS FAILED: $( $effect.Source )"
		exit 1
	}
}

Write-Host "SHADERS OK: $( $effects.Count ) effects compiled with Goo $version"
