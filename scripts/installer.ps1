<#
.SYNOPSIS
Builds Loom's Windows installer: Loom-win-Setup.exe.

.DESCRIPTION
Publishes Loom (scripts/publish.ps1) and packs it with Velopack's vpk tool, restored from
dotnet-tools.json. The installer needs no questions and no administrator: it shows the animated
splash in assets\installer\splash.gif with a progress bar, installs to %LOCALAPPDATA%\Loom,
puts a shortcut on the desktop and starts Loom, which writes its own Start menu shortcut.
Uninstalling from Settings > Apps removes the install folder and the shortcuts, and keeps the
user's data in %LOCALAPPDATA%\Lokn.

The output folder keeps earlier releases, so vpk can build update packages (deltas) against
them. A version can be packed once; raise <Version> in Lokn.csproj, or pass -Version, for the
next one. Delete the folder to start over.

Exits non-zero when the publish or the pack fails.

.EXAMPLE
pwsh scripts/installer.ps1
pwsh scripts/installer.ps1 -Version 0.2.0
#>
param(
	# The release version, such as 0.2.0. Defaults to the Version in Lokn.csproj.
	[string] $Version,
	# The folder the installer and release packages go to. Defaults to artifacts\installer in the repository.
	[string] $Output
)

. "$PSScriptRoot\common.ps1"

if ( -not $Version )
{
	$Version = ( [xml]( Get-Content "$RepoRoot\Lokn.csproj" -Raw ) ).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if ( -not $Output )
{
	$Output = "$RepoRoot\artifacts\installer"
}

$publish = "$RepoRoot\artifacts\publish\win-x64"
& "$PSScriptRoot\publish.ps1" -Output $publish -Version $Version
if ( $LASTEXITCODE -ne 0 )
{
	Write-Host 'INSTALLER FAILED: the publish failed'
	exit $LASTEXITCODE
}

Push-Location $RepoRoot
try
{
	dotnet tool restore | Out-Null
	if ( $LASTEXITCODE -ne 0 )
	{
		Write-Host 'INSTALLER FAILED: dotnet tool restore could not get vpk'
		exit $LASTEXITCODE
	}

	Write-Host "Packing Loom $Version..."
	# The desktop shortcut carries Loom's app ID, so the taskbar groups it with Loom's window.
	# Loom writes the Start menu shortcut itself, with the toast activator Windows needs for notifications.
	dotnet vpk pack `
		--packId Loom `
		--packTitle Loom `
		--runtime win-x64 `
		--packVersion $Version `
		--packDir $publish `
		--mainExe Loom.exe `
		--icon "$RepoRoot\assets\brand\loom.ico" `
		--splashImage "$RepoRoot\assets\installer\splash.gif" `
		--splashProgressColor '#d5a583' `
		--shortcuts Desktop `
		--aumid dev.lokn.desktop `
		--outputDir $Output
	if ( $LASTEXITCODE -ne 0 )
	{
		Write-Host 'INSTALLER FAILED: vpk pack failed'
		exit $LASTEXITCODE
	}
}
finally
{
	Pop-Location
}

$setup = Join-Path $Output 'Loom-win-Setup.exe'
if ( -not ( Test-Path $setup ) )
{
	Write-Host "INSTALLER FAILED: vpk did not write $setup"
	exit 1
}

Write-Host ( 'INSTALLER OK (Loom {0}, {1:N0} MB): {2}' -f $Version, ( ( Get-Item $setup ).Length / 1MB ), $setup )
