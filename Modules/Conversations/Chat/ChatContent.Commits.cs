using System.Text.Json;
using System.Text.RegularExpressions;
using Goo;
using Lokn.Core;
using Lokn.Modules.Git;

namespace Lokn.Modules.Chat;

/// <summary>Shows each commit a shell command made as a card under it: a commit mark, its subject, its branch and short hash, and its counts; a click
/// opens it in the Git panel.</summary>
public sealed partial class ChatContent
{
	/// <summary>Describes one commit a command's output reports.</summary>
	/// <param name="Branch">The branch it went on.</param>
	/// <param name="Hash">Its short hash.</param>
	/// <param name="Subject">Its first message line.</param>
	/// <param name="Summary">Its file and line counts, such as "2 files changed, 10 insertions(+)", or empty.</param>
	private readonly record struct CommitReport( string Branch, string Hash, string Subject, string Summary );

	/// <summary>Reads the commits a command's output reports: git prints <c>[branch hash] subject</c> for each, then a count line.</summary>
	private static List<CommitReport> ParseCommits( string output )
	{
		var commits = new List<CommitReport>();
		var lines = output.Replace( "\r\n", "\n" ).Split( '\n' );
		for ( var i = 0; i < lines.Length; i++ )
		{
			if ( CommitLinePattern().Match( lines[i].Trim() ) is not { Success: true } match )
				continue;
			var summary = i + 1 < lines.Length && lines[i + 1].Contains( "changed", StringComparison.Ordinal ) ? lines[i + 1].Trim() : "";
			commits.Add( new( match.Groups["branch"].Value, match.Groups["hash"].Value, match.Groups["subject"].Value, summary ) );
		}
		return commits;
	}

	/// <summary>Gets the folder a command ran its git in: the folder of its first <c>cd</c>, else its working folder, else the chat's.</summary>
	private string ComputeCommandFolder( JsonElement arguments, string command )
	{
		if ( CdPattern().Match( command ) is { Success: true } cd )
		{
			var path = cd.Groups["path"].Value.Trim( '"', '\'' );
			// Git Bash writes a drive as /f/...; Windows reads it as F:/...
			if ( OperatingSystem.IsWindows() && DrivePattern().Match( path ) is { Success: true } drive )
				path = $"{char.ToUpperInvariant( drive.Groups["d"].Value[0] )}:{drive.Groups["rest"].Value}";
			if ( Path.IsPathFullyQualified( path ) )
				return path;
		}
		return ReadText( arguments, "cwd" ) is { Length: > 0 } cwd && Path.IsPathFullyQualified( cwd ) ? cwd : store.GetDirectory( conversationId );
	}

	/// <summary>Builds one commit's card; a click opens the commit in the Git panel of its folder.</summary>
	private Button BuildCommitCard( string key, CommitReport commit, string folder )
	{
		var theme = Theme.Current;
		void Open() => context.Run( () =>
		{
			if ( context.App.Modules.Any( module => module.Id == "git" ) )
				context.App.GetModule<GitModule>().OpenCommit( context.App, context.Id, folder, commit.Hash );
		} );
		var details = new List<TextSpan>
		{
			new( commit.Branch, new Style { Color = theme.AnsiGreen } ),
			new( "  " + commit.Hash, new Style { Color = theme.AnsiYellow } ),
		};
		if ( commit.Summary.Length > 0 )
			details.Add( new( "  ·  " + commit.Summary, new Style { Color = theme.Muted } ) );

		return new Button
		{
			Key = key,
			AccessibilityName = $"Open commit {commit.Hash}: {commit.Subject}",
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 1.5,
			MinWidth = 0,
			Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
			BackgroundColor = theme.Text.WithAlpha( 0.035 ),
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border.WithAlpha( 0.45 ),
			BorderRadius = theme.Radius * 2,
			Cursor = Cursor.Pointer,
			Focusable = true,
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			OnClick = Open,
			KeyBindings = [new( Key.Enter, KeyModifiers.None, Open ), new( Key.Space, KeyModifiers.None, Open )],
			Children =
			[
				BuildCommitMark(),
				new Container
				{
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					Gap = 2,
					Children =
					[
						new Text
						{
							Content = commit.Subject,
							Color = theme.Text,
							FontWeight = 600,
							MinWidth = 0,
							TextWrap = TextWrap.NoWrap,
							TextTrimming = TextTrimming.Ellipsis,
						},
						new Text( [.. details] )
						{
							FontFamily = ViewStyle.CodeFontFamily,
							FontSize = ViewStyle.SmallSize,
							MinWidth = 0,
							TextWrap = TextWrap.NoWrap,
							TextTrimming = TextTrimming.Ellipsis,
						},
					],
				},
				new Text { Content = "\uE8A7", FontFamily = ViewStyle.IconFontFamily, FontSize = ViewStyle.SmallSize, Color = theme.Muted, FlexShrink = 0 },
			],
		};
	}

	/// <summary>Builds the commit mark: a ring on a line, as graphs draw a commit.</summary>
	private static Container BuildCommitMark()
	{
		var theme = Theme.Current;
		const int size = 28;
		return new Container
		{
			Width = size,
			Height = size,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Accent.WithAlpha( 0.12 ),
			Children =
			[
				new Container { Position = PositionType.Absolute, Left = 4, Right = 4, Top = size / 2 - 1, Height = 2, BackgroundColor = theme.Accent },
				new Container { Width = 10, Height = 10, BorderRadius = 5, BorderWidth = 2, BorderColor = theme.Accent, BackgroundColor = theme.Panel },
			],
		};
	}

	/// <summary>Matches git's line for a new commit: <c>[branch hash] subject</c>, with <c>(root-commit)</c> for a repository's first.</summary>
	[GeneratedRegex( @"^\[(?<branch>[^\s\]]+)(?: \(root-commit\))? (?<hash>[0-9a-f]{7,40})\] (?<subject>.+)$" )]
	private static partial Regex CommitLinePattern();

	/// <summary>Matches a command's first <c>cd</c> and its folder.</summary>
	[GeneratedRegex( @"(?:^|[;&|]\s*)cd\s+(?<path>""[^""]+""|'[^']+'|[^\s;&|]+)" )]
	private static partial Regex CdPattern();

	/// <summary>Matches a Git Bash drive path such as <c>/f/FPDev</c>.</summary>
	[GeneratedRegex( @"^/(?<d>[a-zA-Z])(?<rest>/.*)?$" )]
	private static partial Regex DrivePattern();
}
