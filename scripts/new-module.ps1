<#
.SYNOPSIS
Creates a new hot-reloaded source module from a minimal template.

.DESCRIPTION
Creates <Root>\<Name>\<Name>Module.cs containing one module with one panel. A
running Lokn compiles and loads it within about a second; no build or restart is
needed. The panel then appears in the palette as "Open <Name>".

Fails without changing anything when the module folder already exists. Read the
AGENTS.md in the modules folder for the authoring rules before editing the module.

.EXAMPLE
pwsh scripts/new-module.ps1 -Name Notes
#>
param(
	# The module name in PascalCase, letters and digits only. Its lowercase form becomes the module ID.
	[Parameter( Mandatory )]
	[ValidatePattern( '^[A-Z][A-Za-z0-9]*$' )]
	[string] $Name,

	# The modules folder. Defaults to the one Lokn watches.
	[string] $Root = ( Join-Path $env:LOCALAPPDATA 'Lokn\modules' )
)

. "$PSScriptRoot\common.ps1"

$id = $Name.ToLowerInvariant()
$reserved = @( 'workspace', 'workspaces', 'home', 'omp', 'conversations', 'worktrees', 'hotload', 'welcome' )
if ( $reserved -contains $id )
{
	Write-Host "'$id' is the ID of a built-in module. Choose another name."
	exit 1
}

$directory = Join-Path $Root $Name
if ( Test-Path $directory )
{
	Write-Host "Module folder already exists: $directory"
	exit 1
}

$source = @"
using System.Text.Json;
using Goo;
using Lokn.Core;

/// <summary>Contributes the $Name panel.</summary>
public sealed class ${Name}Module : ILoknModule
{
	/// <summary>Gets the stable module identifier used to namespace contributions.</summary>
	public string Id => "$id";

	/// <summary>Gets the feature's display name.</summary>
	public string Title => "$Name";

	/// <summary>Registers the panel and its automatic "Open $Name" palette command.</summary>
	public void Register( ModuleBuilder builder )
	{
		builder.Panel( "main", "$Name", context => new ${Name}Content( context ) );
	}
}

/// <summary>Owns the $Name panel's state for as long as the panel is open.</summary>
internal sealed class ${Name}Content( PanelContext context ) : PanelContent
{
	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Retains the entered text; restored after a reload or restart.</summary>
	private readonly State<string> _text = new( context.RestoredState?.GetString() ?? "" );

	/// <summary>Builds a fresh declaration from the retained state.</summary>
	public override Blob Build()
	{
		return new Container
		{
			Padding = ContentPadding,
			Gap = 8,
			Children =
			[
				new Text
				{
					Content = "$Name",
					FontSize = 22,
				},
				new TextEntry
				{
					Value = _text.Value,
					Controlled = true,
					Placeholder = "Type something. It survives saving this file.",
					OnChange = value => context.Run( () => _text.Value = value ),
				},
			],
		};
	}

	/// <summary>Captures the state a reload or restart should bring back.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _text.Value );
}
"@

New-Item -ItemType Directory -Force $directory | Out-Null
$file = Join-Path $directory "${Name}Module.cs"
[IO.File]::WriteAllText( $file, ( $source -replace "`r?`n", "`r`n" ) + "`r`n", [Text.UTF8Encoding]::new( $false ) )

Write-Host "CREATED $file"
Write-Host "Module ID '$id', panel type '$id.main', palette command 'Open $Name'."
Write-Host "Check it compiles with: pwsh scripts/check-module.ps1 -Name $Name"
