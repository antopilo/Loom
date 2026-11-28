using System.Globalization;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Overview;

/// <summary>Draws the top of a project's brief: the day's painting under "The &lt;day&gt; Brief", the date and time set
/// along the margins, and one line on what matters in the project, written by the profile's small model.</summary>
/// <remarks>The clock ticks on the minute; each tick also changes the painting at midnight and asks for a new line once the last one is
/// <see cref="BriefLifetime"/> old, or <see cref="BriefRewrite"/> old when what waits or works in the project changed. Until a line comes, or
/// without a model, the page states the plain facts.</remarks>
internal sealed partial class ProjectOverviewContent
{
	/// <summary>Defines how long a written line holds before it is asked for again.</summary>
	private static readonly TimeSpan BriefLifetime = TimeSpan.FromHours( 1 );

	/// <summary>Defines how soon a line is asked for again when the project's waiting or working chats changed.</summary>
	private static readonly TimeSpan BriefRewrite = TimeSpan.FromMinutes( 10 );

	/// <summary>Gets the hero title's color, the theme's accent, which the shade behind it keeps readable on every painting.</summary>
	private static Color TitleColor => BriefPaper.Current.Accent;

	/// <summary>Defines how dark the shade behind the hero title is at its middle, as black's opacity.</summary>
	private const double TitleShade = 0.32;

	/// <summary>Defines the room the date and the time take along a margin, in logical pixels.</summary>
	private const int RailTextLength = 320;

	/// <summary>Defines the height of the line above the painting with the project's name, folder and branch, in logical pixels.</summary>
	private const int KickerHeight = 34;

	/// <summary>Defines the most characters of a written line that show.</summary>
	private const int BriefMaxLength = 280;

	/// <summary>Defines how many of the project's other chats, designs, reports and rows of each added section the model reads.</summary>
	private const int BriefMaterialLimit = 8;

	/// <summary>Defines the question that asks the small model for the line under the painting.</summary>
	internal const string BriefQuestion = "Reply with only the opening line of today's brief for this software project, as a calm newspaper editor would write it: "
		+ "one or two plain sentences, at most 35 words, on what matters now (what waits for the user, what the agents do, what changed lately) and a good "
		+ "focus. Name chats by their titles when it helps. Use only facts from the material. No markdown, no lists, no greeting, no quotes, no date.";

	/// <summary>Defines the last line of the material: the task again, with an example of the voice.</summary>
	private const string BriefCue = "Write the brief line now: one or two plain sentences, at most 35 words, in the calm voice of a newspaper editor. For example: "
		+ "\"A quiet Friday: nothing waits for you. The landing design changed just now, and Tidy the parser is the freshest thread to pick up.\"";

	/// <summary>Stores the time the page shows, set on each minute.</summary>
	private readonly State<DateTimeOffset> _now = new( DateTimeOffset.Now );

	/// <summary>Stores the day's painting once decoded, or null before; the page owns and disposes it.</summary>
	private readonly State<ImageSource?> _painting = new( null );

	/// <summary>Stores the day whose painting is shown or decoding.</summary>
	private DayOfWeek? _paintingDay;

	/// <summary>Stores the line the model wrote for the project, or empty for none yet.</summary>
	private readonly State<string> _brief = new( "" );

	/// <summary>Stores whether a line is being asked for.</summary>
	private bool _isAskingBrief;

	/// <summary>Gets the key of the page's project in <see cref="OverviewModule.Briefs"/>: its host and id.</summary>
	internal string BriefKey => $"{_context.Host.Id}/{_projectId.Value:N}";

	/// <summary>Shows the day's painting, starts the clock and asks for the project's line.</summary>
	private void StartBrief()
	{
		UpdatePainting();
		_ = TickAsync();
		_context.Post( RefreshBrief );
	}

	/// <summary>Sets the time on each minute until the page closes, changing the painting and the line when they are due.</summary>
	private async Task TickAsync()
	{
		try
		{
			while ( true )
			{
				var now = DateTimeOffset.Now;
				await Task.Delay( TimeSpan.FromMilliseconds( 60_050 - now.Second * 1000 - now.Millisecond ), _context.Lifetime );
				_context.Post( () =>
				{
					_now.Value = DateTimeOffset.Now;
					UpdatePainting();
					RefreshBrief();
				} );
			}
		}
		catch ( OperationCanceledException )
		{
			// The page closed.
		}
	}

	/// <summary>Decodes the painting of the shown day off the UI thread when the day changed.</summary>
	private void UpdatePainting()
	{
		var day = _now.Value.DayOfWeek;
		if ( _paintingDay == day )
			return;
		_paintingDay = day;
		_ = LoadPaintingAsync( DailyPainting.ForDay( day ), day );
	}

	/// <summary>Decodes a painting and shows it, unless the day or the page moved on meanwhile; the painting it replaces is released after the page
	/// rebuilt without it.</summary>
	private async Task LoadPaintingAsync( Painting painting, DayOfWeek day )
	{
		ImageSource source;
		try
		{
			source = await Task.Run( () => DailyPainting.Decode( painting ), _context.Lifetime );
		}
		catch ( OperationCanceledException )
		{
			return;
		}
		catch ( Exception error ) when ( error is IOException or InvalidDataException or InvalidOperationException )
		{
			_context.Post( () => _status.Value = $"Could not show today's painting: {error.Message}" );
			return;
		}

		_context.Post( () =>
		{
			if ( IsClosed || _paintingDay != day )
			{
				source.Dispose();
				return;
			}
			var previous = _painting.Value;
			_painting.Value = source;
			if ( previous is not null )
				_context.Post( previous.Dispose );
		} );
	}

	/// <summary>Shows the project's last written line and asks for a new one when it is due and a model is there to write it.</summary>
	private void RefreshBrief()
	{
		if ( IsClosed || _isAskingBrief || ChatHost is not { } host || _projectId.Value is not { } id || host.Projects.Find( id ) is not { } project )
			return;

		var chats = ComputeChats( host, project );
		var sections = ReadSections( project );
		// What waits or works, in chats and in the sections other modules add, so a change there asks for a new line sooner.
		var fingerprint = string.Join( ",", chats.Where( chat => chat.State != ChatState.Idle ).Select( chat => $"{chat.Conversation.Id:N}:{(int)chat.State}" )
			.Concat( sections.SelectMany( entry => entry.Content.Items.Where( item => item.NeedsYou ).Select( item => $"{entry.Section.Id}:{item.Key}" ) ) ) );
		var now = DateTimeOffset.Now;
		var key = BriefKey;
		if ( _module.Briefs.TryGetValue( key, out var line ) )
		{
			_brief.Value = line.Text;
			var age = now - line.Written;
			if ( age < BriefRewrite || (age < BriefLifetime && line.Fingerprint == fingerprint) )
				return;
		}
		if ( _context.App.Services.Get<IAgentService>() is not { } agent )
			return;
		_isAskingBrief = true;
		_ = AskBriefAsync( agent, project.Profile, key, fingerprint, ComposeBriefMaterial( host, project, chats, sections, now ) );
	}

	/// <summary>Asks the small model for the project's line and keeps it for every page of the project; a failure keeps the line written before,
	/// and waits <see cref="BriefRewrite"/> before asking again.</summary>
	private async Task AskBriefAsync( IAgentService agent, SettingsProfile profile, string key, string fingerprint, string material )
	{
		var text = "";
		try
		{
			text = CleanBrief( await agent.AskSmallAsync( profile, BriefQuestion, material, _context.Lifetime ).ConfigureAwait( false ) );
		}
		catch ( Exception error ) when ( error is not OutOfMemoryException )
		{
			// The line is a convenience: the page keeps the plain facts, or the line written before.
		}

		_context.Post( () =>
		{
			_isAskingBrief = false;
			if ( text.Length == 0 && _module.Briefs.TryGetValue( key, out var previous ) )
				text = previous.Text;
			_module.Briefs[key] = new( text, DateTimeOffset.Now, fingerprint );
			if ( BriefKey == key )
				_brief.Value = text;
		} );
	}

	/// <summary>Writes what the model reads about the project: the time, its waiting, working and other chats, its latest designs and reports, and the
	/// rows of the sections other modules add.</summary>
	private string ComposeBriefMaterial( ChatHost host, ProjectInfo project, IReadOnlyList<ChatEntry> chats,
		IReadOnlyList<(BriefSection Section, BriefContent Content)> sections, DateTimeOffset now )
	{
		var lines = new List<string>
		{
			$"Project: {project.Name}",
			"Now: " + now.ToString( "dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture ),
		};
		if ( _branch.Value.Length > 0 )
			lines.Add( $"Branch: {_branch.Value}" );

		void Add( string heading, IEnumerable<string> items )
		{
			var list = items.Take( BriefMaterialLimit ).ToList();
			lines.Add( "" );
			lines.Add( list.Count == 0 ? $"{heading}: none" : $"{heading}:" );
			lines.AddRange( list.Select( item => "- " + item ) );
		}

		var utc = now.ToUniversalTime();
		Add( "Chats waiting for the user's answer", chats.Where( chat => chat.State == ChatState.Waiting ).Select( chat => chat.Conversation.Title ) );
		Add( "Chats whose agent works now", chats.Where( chat => chat.State == ChatState.Working ).Select( chat => chat.Conversation.Title ) );
		Add( "Other chats and tasks, last used first", chats.Where( chat => chat.State == ChatState.Idle )
			.Select( chat => $"{chat.Conversation.Title} ({FormatAge( chat.Conversation.UpdatedAt, utc )})" ) );
		Add( "Designs", _designs.Value.Select( design => $"{design.Name} ({FormatAge( design.Modified, utc )})" ) );
		Add( "Reports", ComputeReports( host, chats ).Select( report => $"{report.Title} ({FormatAge( report.Created, utc )})" ) );
		foreach ( var (section, content) in sections )
			Add( section.Title, content.Items.Select( item => $"{item.Title} ({item.Detail}{(item.NeedsYou ? ", needs the user" : "")})" ) );
		// The material comes after the question, so a small model reads the task last too and writes the line rather than a summary of the lists.
		lines.Add( "" );
		lines.Add( BriefCue );
		return string.Join( "\n", lines );
	}

	/// <summary>Makes text one plain paragraph: no Markdown marks, list dashes, surrounding quotes or extra spaces, cut at a word to
	/// <paramref name="maxLength"/>; for the written line and for a chat's last message under its headline.</summary>
	internal static string CleanBrief( string answer, int maxLength = BriefMaxLength )
	{
		var words = answer.Replace( "**", "", StringComparison.Ordinal ).Replace( "`", "", StringComparison.Ordinal ).Replace( "#", "", StringComparison.Ordinal )
			.Split( (char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries )
			.Where( word => word is not ("-" or "*" or "•") );
		var text = string.Join( ' ', words ).Trim( '"', '\'', '“', '”', ' ' );
		if ( text.Length <= maxLength )
			return text;
		var cut = text.LastIndexOf( ' ', maxLength );
		return text[..(cut > 0 ? cut : maxLength)].TrimEnd( ',', ';', ':', ' ' ) + "…";
	}

	/// <summary>States the plain facts of the project, shown until the model writes its line, or without a model: the chats that wait or work, and the
	/// rows of added sections that need the user.</summary>
	private static string ComputeFacts( IReadOnlyList<ChatEntry> chats, IReadOnlyList<(BriefSection Section, BriefContent Content)> sections, DateTimeOffset now )
	{
		var parts = new List<string>();
		var waiting = chats.Count( chat => chat.State == ChatState.Waiting );
		var working = chats.Count( chat => chat.State == ChatState.Working );
		if ( waiting > 0 )
			parts.Add( waiting == 1 ? "One chat waits for you." : $"{waiting} chats wait for you." );
		if ( working > 0 )
			parts.Add( working == 1 ? "One agent is at work." : $"{working} agents are at work." );
		foreach ( var (section, content) in sections )
		{
			var count = content.Items.Count( item => item.NeedsYou );
			if ( count > 0 )
				parts.Add( $"{section.Title}: {count} need{(count == 1 ? "s" : "")} you." );
		}
		if ( parts.Count > 0 )
			return string.Join( " ", parts );
		return chats.Count == 0
			? "A new project. Type below to start its first chat."
			: $"Nothing waits for you. Last change {FormatAge( chats.Max( chat => chat.Conversation.UpdatedAt ), now )}.";
	}

	/// <summary>Builds the line above the painting: the project's icon, its name in small capitals, then its folder and branch.</summary>
	private Blob BuildKicker( ChatHost host, ProjectInfo project )
	{
		var paper = BriefPaper.Current;
		var icon = _context.App.GetModule<ProjectsModule>().BuildProjectIcon( project, host.Id, 20 );
		var folder = host.IsLocal ? FormatDirectory( project.Directory ) : $"{project.Directory}  ·  {host.Name}";
		if ( _branch.Value.Length > 0 )
			folder += $"  ·  \u2387 {_branch.Value}";
		return new Row
		{
			Key = "kicker",
			Height = KickerHeight,
			AlignItems = AlignItems.Center,
			Gap = 10,
			MinWidth = 0,
			Padding = new Edges( 0, 0, 12, 0 ),
			Children =
			[
				.. icon is null ? Array.Empty<Blob>() : [icon],
				new Text
				{
					Key = "name",
					Content = project.Name.ToUpper( CultureInfo.CurrentCulture ),
					FontSize = DetailSize,
					FontWeight = 600,
					LetterSpacing = 1.6,
					Color = paper.Ink,
					FlexShrink = 0,
					TextMaxLines = 1,
				},
				new Text { Key = "folder", Content = folder, FontSize = DetailSize, Color = paper.Ink3, MinWidth = 0, FlexShrink = 1, TextMaxLines = 1, TextTrimming = TextTrimming.Ellipsis },
			],
		};
	}

	/// <summary>Builds the day's painting with "The &lt;day&gt; Brief" over it, on a soft shade that keeps the title readable on a light sky; a plain card
	/// stands in while the painting decodes.</summary>
	/// <remarks>The shade is a radial gradient rather than a text shadow: Goo draws a wide text shadow blur as stepped copies of the glyphs.</remarks>
	private Blob BuildHero( PageLayout layout )
	{
		var height = layout.HeroHeight;
		var day = _now.Value.ToString( "dddd", CultureInfo.CurrentCulture );
		day = day.Length > 0 ? char.ToUpper( day[0], CultureInfo.CurrentCulture ) + day[1..] : day;
		Blob[] picture = _painting.Value is { } source
			?
			[
				new Image { Key = "painting", Source = source, Fit = ImageFit.Cover, Position = PositionType.Absolute, Left = 0, Top = 0, Right = 0, Bottom = 0 },
				new Container
				{
					Key = "shade",
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Right = 0,
					Bottom = 0,
					BackgroundGradient = new RadialGradient( Color.Black.WithAlpha( TitleShade ), Color.Black.WithAlpha( TitleShade / 2 ), Color.Black.WithAlpha( 0 ) ),
				},
			]
			: [];
		return new Container
		{
			Key = "hero",
			Height = height,
			FlexShrink = 0,
			BorderRadius = 3,
			Overflow = Overflow.Hidden,
			BackgroundColor = BriefPaper.Current.Card,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"The {day} Brief. {DailyPainting.ForDay( _now.Value.DayOfWeek ).Credit}",
			Children =
			[
				.. picture,
				new Column
				{
					Key = "title",
					AlignItems = AlignItems.Center,
					Padding = new Edges( 0, 16 ),
					Color = TitleColor,
					Children =
					[
						new Text { Key = "the", Content = "The", FontFamily = BriefArt.DisplayFontFamily, FontStyle = FontStyle.Italic, FontSize = Math.Round( height * 0.14 ), LineHeight = 1 },
						new Text
						{
							Key = "day",
							Content = $"{day} Brief",
							FontFamily = BriefArt.DisplayFontFamily,
							FontSize = Math.Round( height * 0.22 ),
							LineHeight = 1.05,
							TextAlign = TextAlign.Center,
							TextWrap = TextWrap.Wrap,
						},
					],
				},
			],
		};
	}

	/// <summary>Builds the line under the painting, in an italic serif, beside the painting's credit; narrow, the credit goes under it.</summary>
	private Blob BuildLede( IReadOnlyList<ChatEntry> chats, IReadOnlyList<(BriefSection Section, BriefContent Content)> sections, PageLayout layout )
	{
		var paper = BriefPaper.Current;
		var lede = new Text
		{
			Key = "line",
			Content = _brief.Value is { Length: > 0 } written ? written : ComputeFacts( chats, sections, DateTimeOffset.UtcNow ),
			FontFamily = BriefArt.SerifFontFamily,
			FontStyle = FontStyle.Italic,
			FontSize = 17,
			LineHeight = 1.45,
			Color = paper.Ink2,
			TextWrap = TextWrap.Wrap,
			MinWidth = 0,
			Selectable = true,
		};
		var credit = new Text
		{
			Key = "credit",
			Content = DailyPainting.ForDay( _now.Value.DayOfWeek ).Credit,
			FontFamily = ViewStyle.CodeFontFamily,
			FontSize = 10,
			LineHeight = 1.5,
			Color = paper.Ink3,
			TextWrap = TextWrap.Wrap,
			MinWidth = 0,
		};
		if ( !layout.IsWide )
			return new Column { Key = "lede", Gap = 6, Padding = new Edges( 14, 0, 0, 0 ), Children = [lede, credit] };

		lede.FlexGrow = 3;
		lede.FlexBasis = 0;
		credit.FlexGrow = 2;
		credit.FlexBasis = 0;
		credit.TextAlign = TextAlign.Right;
		credit.Padding = new Edges( 4, 0, 0, 0 );
		return new Row { Key = "lede", Gap = 40, AlignItems = AlignItems.FlexStart, Padding = new Edges( 14, 0, 0, 0 ), Children = [lede, credit] };
	}

	/// <summary>Builds one margin beside the column with a text set along it, turned to face the column, level with the painting's middle.</summary>
	private static Column BuildRail( string key, string text, double rotation, PageLayout layout ) => new()
	{
		Key = key,
		Width = RailWidth,
		FlexShrink = 0,
		AlignItems = AlignItems.Center,
		Padding = new Edges( Math.Max( 0, KickerHeight + layout.HeroHeight / 2 - RailTextLength / 2.0 ), 0, 0, 0 ),
		Children =
		[
			new Container
			{
				Width = RailWidth,
				Height = RailTextLength,
				AlignItems = AlignItems.Center,
				JustifyContent = JustifyContent.Center,
				Children =
				[
					new Text
					{
						Content = text,
						Width = RailTextLength,
						FlexShrink = 0,
						TextAlign = TextAlign.Center,
						TextMaxLines = 1,
						FontFamily = BriefArt.SerifFontFamily,
						FontStyle = FontStyle.Italic,
						FontSize = 32,
						Color = BriefPaper.Current.Ink,
						Transform = new PanelTransform { Rotate = rotation },
					},
				],
			},
		],
	};
}
