<#
.SYNOPSIS
Publishes a Loom release to GitHub: the app, and the modules whose version is new.

.DESCRIPTION
Builds the installer with scripts/installer.ps1 and uploads it as the GitHub release v<Version>,
where installed copies of Loom find it through Velopack. The latest earlier release is downloaded
first, so vpk also builds a small delta package from it.

Then every module whose <Version> is not in the feed yet is published as a module update: its DLL
and an updated modules.json go to the pre-release tagged 'modules'. Velopack reads stable releases
only, so it ignores that release.

-ModulesOnly publishes module updates alone, built from the current source. An installed Loom loads
a module update only when every type and member the module uses exists in that Loom; elsewhere the
bundled module keeps running. A module that uses Core API newer than the latest app release reaches
users with the next app release; the script warns when Core changed since then.

To publish a module, raise <Version> in Modules/<Name>/Lokn.Modules.<Name>.csproj.
To publish the app, raise <Version> in Lokn.csproj.

Needs a GitHub token that can write to the repository: -Token, else $env:GITHUB_TOKEN, else the
one Git's credential manager keeps for github.com. Refuses to run with uncommitted or unpushed
changes, because the release is tagged at the pushed commit. -DryRun builds everything and lists
what would be published, without uploading.

.github/workflows/release.yml runs this script on GitHub Actions when a v<Version> tag is pushed,
or from "Run workflow". There the checkout is the commit to release, and a pushed tag must match
<Version> in Lokn.csproj.

Exits non-zero when the build, a download or an upload fails.

.EXAMPLE
pwsh scripts/release.ps1 -DryRun

.EXAMPLE
pwsh scripts/release.ps1

.EXAMPLE
pwsh scripts/release.ps1 -ModulesOnly
#>
param(
	# The GitHub repository installed copies of Loom update from; the Update source setting's default.
	[string] $Repository = 'https://github.com/antopilo/Loom',
	# A GitHub token with write access. Defaults to $env:GITHUB_TOKEN, then Git's stored credential.
	[string] $Token,
	# Publishes module updates only, not the app.
	[switch] $ModulesOnly,
	# Builds and lists what would be published, without uploading.
	[switch] $DryRun
)

. "$PSScriptRoot\common.ps1"

$Version = ( [xml]( Get-Content "$RepoRoot\Lokn.csproj" -Raw ) ).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$slug = ( [uri]$Repository ).AbsolutePath.Trim( '/' ) -replace '\.git$', ''
$api = "https://api.github.com/repos/$slug"
$output = "$RepoRoot\artifacts\release"
$modules = "$RepoRoot\artifacts\publish\win-x64\modules"

function Stop-Release( [string] $Reason )
{
	Write-Host "RELEASE FAILED: $Reason"
	exit 1
}

# Calls the GitHub API; returns null for 404, so a missing release reads as absent.
function Invoke-GitHub( [string] $Method, [string] $Uri, [object] $Body )
{
	$arguments = @{ Method = $Method; Uri = $Uri; Headers = $script:headers }
	if ( $Body )
	{
		$arguments.Body = $Body | ConvertTo-Json -Depth 5
		$arguments.ContentType = 'application/json'
	}

	try
	{
		Invoke-RestMethod @arguments
	}
	catch
	{
		if ( $_.Exception.Response.StatusCode -eq 404 )
		{
			return $null
		}

		throw
	}
}

# A release is tagged at the pushed commit, so it must hold exactly what is committed and pushed.
# On GitHub Actions the checkout is that commit, often a detached tag with no upstream to compare.
$isCi = $env:GITHUB_ACTIONS -eq 'true'
if ( -not $DryRun )
{
	if ( git -C $RepoRoot status --porcelain )
	{
		Stop-Release 'commit or stash your changes first; the release is tagged at the pushed commit'
	}

	if ( -not $isCi )
	{
		git -C $RepoRoot fetch --quiet
		if ( [int]( git -C $RepoRoot rev-list --count '@{u}..HEAD' ) -gt 0 )
		{
			Stop-Release 'push your commits first; the release is tagged at the pushed commit'
		}
	}
}

# A pushed tag names the version it releases, so it must be the version Lokn.csproj builds.
if ( $isCi -and $env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$Version" )
{
	Stop-Release "tag $env:GITHUB_REF_NAME does not match <Version> $Version in Lokn.csproj"
}

if ( -not $Token )
{
	$Token = $env:GITHUB_TOKEN
}

if ( -not $Token )
{
	# Git's credential manager keeps the token Git pushes with; never ask for one interactively here.
	$env:GCM_INTERACTIVE = 'never'
	$env:GIT_TERMINAL_PROMPT = '0'
	$Token = ( "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null | Where-Object { $_ -like 'password=*' } ) -replace '^password=', ''
}

if ( -not $Token -and -not $DryRun )
{
	Stop-Release 'no GitHub token; pass -Token or set GITHUB_TOKEN'
}

$script:headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
if ( $Token )
{
	$script:headers.Authorization = "Bearer $Token"
}

Push-Location $RepoRoot
try
{
	dotnet tool restore | Out-Null
	if ( $LASTEXITCODE -ne 0 )
	{
		Stop-Release 'dotnet tool restore could not get vpk'
	}

	# The app: build the installer next to the latest earlier release, so vpk adds a delta package, then upload it as v<Version>.
	if ( -not $ModulesOnly )
	{
		if ( $Token -and ( Invoke-GitHub Get "$api/releases/tags/v$Version" ) )
		{
			# A release made on a PC pushes its tag, which starts the workflow again; that run has nothing to do.
			if ( $isCi -and $env:GITHUB_REF_TYPE -eq 'tag' )
			{
				Write-Host "RELEASE SKIPPED: Loom $Version is already released"
				exit 0
			}

			Stop-Release "Loom $Version is already released; raise <Version> in Lokn.csproj"
		}

		if ( Test-Path $output )
		{
			Remove-Item $output -Recurse -Force
		}

		New-Item -ItemType Directory $output | Out-Null
		if ( $Token )
		{
			dotnet vpk download github --repoUrl $Repository --token $Token --outputDir $output | Out-Null
			if ( $LASTEXITCODE -ne 0 )
			{
				Write-Warning 'No earlier release was downloaded, so this release has no delta package.'
			}
		}

		& "$PSScriptRoot\installer.ps1" -Version $Version -Output $output
		if ( $LASTEXITCODE -ne 0 )
		{
			Stop-Release 'the installer build failed'
		}

		if ( -not $DryRun )
		{
			dotnet vpk upload github --repoUrl $Repository --token $Token --outputDir $output --publish --releaseName "Loom $Version" --tag "v$Version" --targetCommitish ( git -C $RepoRoot rev-parse HEAD )
			if ( $LASTEXITCODE -ne 0 )
			{
				Stop-Release 'vpk upload failed'
			}
		}
	}
	else
	{
		& "$PSScriptRoot\publish.ps1"
		if ( $LASTEXITCODE -ne 0 )
		{
			Stop-Release 'the publish failed'
		}

		git fetch --tags --quiet
		$last = git describe --tags --abbrev=0 --match 'v*' 2>$null
		if ( $last )
		{
			git diff --quiet $last -- Core
			if ( $LASTEXITCODE -ne 0 )
			{
				Write-Warning "Core changed since $last. A module update that uses the new Core API is refused by Loom $last, which keeps its bundled module, until the next app release."
			}
		}
	}
}
finally
{
	Pop-Location
}

# The modules: read the published index, then add every module whose version it lacks.
$release = if ( $Token ) { Invoke-GitHub Get "$api/releases/tags/modules" }
$indexAsset = $release.assets | Where-Object name -EQ 'modules.json'
$published = @()
if ( $indexAsset )
{
	$file = New-TemporaryFile
	Invoke-WebRequest -Uri $indexAsset.url -Headers ( $script:headers + @{ Accept = 'application/octet-stream' } ) -OutFile $file
	$published = @( ( Get-Content $file -Raw | ConvertFrom-Json ).modules | Where-Object { $_ } )
	Remove-Item $file
}
elseif ( -not $Token )
{
	Write-Warning 'Without a token the published modules cannot be read; every module is listed as new.'
}

$new = @( Get-ChildItem $modules -Filter 'Lokn.Modules.*.dll' | ForEach-Object {
	$name = $_.BaseName
	$moduleVersion = [Reflection.AssemblyName]::GetAssemblyName( $_.FullName ).Version.ToString( 3 )
	$hash = ( Get-FileHash $_.FullName -Algorithm SHA256 ).Hash
	$same = $published | Where-Object { $_.name -eq $name -and $_.version -eq $moduleVersion }
	if ( -not $same )
	{
		[pscustomobject]@{ name = $name; version = $moduleVersion; file = "$name-$moduleVersion.dll"; sha256 = $hash; app = $Version; path = $_.FullName }
	}
	elseif ( $same.sha256 -ne $hash )
	{
		Write-Warning "$name changed since $moduleVersion was published. Raise <Version> in its project to ship the change as a module update; otherwise it ships with the next app release."
	}
} )

if ( $new.Count -gt 0 -and -not $DryRun )
{
	if ( -not $release )
	{
		$release = Invoke-GitHub Post "$api/releases" @{
			tag_name = 'modules'
			name = 'Module updates'
			prerelease = $true
			body = "Module DLLs and modules.json for Loom's module updates. Installed copies of Loom read this release; do not delete it."
		}
	}

	$upload = $release.upload_url -replace '\{.*\}$', ''
	foreach ( $module in $new )
	{
		Invoke-RestMethod -Method Post -Uri "$( $upload )?name=$( $module.file )" -Headers $script:headers -InFile $module.path -ContentType 'application/octet-stream' | Out-Null
	}

	# The index lists every published version, old ones too, so an older Loom still finds a module version that fits it.
	$index = New-TemporaryFile
	@{ modules = @( $published ) + @( $new | Select-Object name, version, file, sha256, app ) } | ConvertTo-Json -Depth 5 | Set-Content $index -NoNewline
	if ( $indexAsset )
	{
		Invoke-RestMethod -Method Delete -Uri $indexAsset.url -Headers $script:headers | Out-Null
	}

	Invoke-RestMethod -Method Post -Uri "$( $upload )?name=modules.json" -Headers $script:headers -InFile $index -ContentType 'application/json' | Out-Null
	Remove-Item $index
}

$moduleList = if ( $new.Count -gt 0 ) { ( $new | ForEach-Object { "$( $_.name -replace '^Lokn\.Modules\.', '' ) $( $_.version )" } ) -join ', ' } else { 'none' }
$app = if ( $ModulesOnly ) { 'not released' } else { "Loom $Version" }
if ( $DryRun )
{
	Write-Host "RELEASE DRY RUN OK: would publish app: $app; module updates: $moduleList"
}
else
{
	Write-Host "RELEASE OK: app: $app; module updates: $moduleList"
}
