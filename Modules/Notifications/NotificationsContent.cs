using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Notifications;

/// <summary>Selects what one row of the notification feed shows.</summary>
internal enum NotificationRowKind
{
	Day, Message, Activity
}

/// <summary>Names where a notification came from, which picks its icon.</summary>
internal enum NotificationSource
{
	Agent, Browser, Other
}

/// <summary>Describes one row of the notification feed; a row equal to the one already mounted under its key is not built again.</summary>
/// <param name="Key">The stable row key.</param>
/// <param name="Kind">A day separator, one notification read like an agent's reply, or a run of short updates folded into one line.</param>
/// <param name="Label">The day's name for a separator, or the run's summary.</param>
/// <param name="Items">The notifications the row shows, oldest first; none for a separator.</param>
/// <param name="Gap">The space above the row in logical pixels.</param>
/// <param name="Hovered">The notification under the pointer when it is in this row, whose time shows.</param>
/// <param name="IsExpanded">Whether a run is open onto its notifications.</param>
internal sealed record NotificationRow( string Key, NotificationRowKind Kind, string Label, IReadOnlyList<LoknNotification> Items, double Gap, Guid? Hovered = null, bool IsExpanded = false ) : IMeasuredRow
{
	/// <summary>Compares rows by what they show, including each notification's read state, rather than by list identity.</summary>
	public bool Equals( NotificationRow? other ) => other is not null && Key == other.Key && Kind == other.Kind && Label == other.Label && Gap == other.Gap
		&& Hovered == other.Hovered && IsExpanded == other.IsExpanded && Items.SequenceEqual( other.Items );

	/// <summary>Hashes the row's identity and shape.</summary>
	public override int GetHashCode() => HashCode.Combine( Key, Kind, Label, Items.Count, Hovered, IsExpanded );
}

/// <summary>Shows notifications the way a conversation reads, in the feed's workspace and in the popover under the titlebar bell.</summary>
/// <remarks>
/// It reuses the chat transcript: the reading column, text sizes, Markdown, the virtual list that follows the newest row, and
/// the scrollbar. A notification with something to read, such as an agent's closing message, shows like a reply: its source's
/// icon and name, then its text. Short updates in a row, such as GitHub checks or chats finishing, fold into one summary line,
/// such as "2 checks passed, Build fix finished", which opens like a chat's tool calls. A row's time shows in the margin
/// while the pointer is on it. Opening a notification marks it read and shows its conversation's workspace, its panel, or its link.
/// </remarks>
internal sealed class NotificationsContent( PanelContext context, Action? dismiss = null ) : PanelContent
{
	/// <summary>Defines how far apart two short updates may be and still fold into one run.</summary>
	private static readonly TimeSpan GroupWindow = TimeSpan.FromMinutes( 10 );

	/// <summary>Defines the longest single-line message still read as a short update rather than a reply.</summary>
	private const int ShortLimit = 72;

	/// <summary>Defines the height assumed for a row until layout measures it, in logical pixels.</summary>
	private const double EstimatedRowHeight = 40;

	/// <summary>Defines the source icon's size in logical pixels.</summary>
	private const int SourceIconSize = 14;

	/// <summary>Defines the margin that holds a hovered row's time, in logical pixels.</summary>
	private const int TimeGutter = 60;

	/// <summary>Defines the unread dot's diameter in logical pixels.</summary>
	private const int UnreadDotSize = 7;

	/// <summary>Defines the emphasized weight of source names.</summary>
	private const int SourceWeight = 600;

	/// <summary>Defines the popover header's height in logical pixels.</summary>
	private const int HeaderHeight = 44;

	/// <summary>Defines the Segoe Fluent Robot glyph, for agents.</summary>
	internal const string AgentGlyph = "\uE99A";

	/// <summary>Defines the Segoe Fluent Globe glyph, for the browser.</summary>
	internal const string BrowserGlyph = "\uE774";

	/// <summary>Defines the Segoe Fluent Ringer glyph, for any other source and the sidebar's Notifications row.</summary>
	internal const string BellGlyph = "\uEA8F";

	/// <summary>Defines the Segoe Fluent FullScreen glyph that opens the full feed from the popover.</summary>
	private const string FullFeedGlyph = "\uE740";

	/// <summary>Defines the Segoe Fluent Cancel glyph that removes notifications.</summary>
	private const string RemoveGlyph = "\uE711";

	/// <summary>Targets the feed's scroller and keeps it on the newest notification until the reader scrolls up.</summary>
	private readonly TranscriptFollow _follow = new();

	/// <summary>Tracks the notification under the pointer, whose time shows in the margin.</summary>
	private readonly State<Guid> _hovered = new( Guid.Empty );

	/// <summary>Tracks the runs opened onto their notifications, by row key.</summary>
	private readonly State<HashSet<string>> _expanded = new( [] );

	/// <summary>Builds one row; kept as one delegate so the virtual list reuses unchanged rows.</summary>
	private Func<NotificationRow, Blob>? _buildRow;

	/// <summary>Gets whether this is the popover under the bell rather than the feed's panel.</summary>
	private bool IsPopover => dismiss is not null;

	/// <summary>Builds the feed in the reading column, with its title and actions: above it in the popover, around it in the panel.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var feed = context.App.Notifications;
		var items = feed.Items;
		var unread = feed.UnreadCount;
		_buildRow ??= BuildRow;
		var hovered = _hovered.Value;
		var expanded = _expanded.Value;
		var rows = ComputeRows( items, DateTimeOffset.Now, context.App.Extensions.Get<NotificationKind>( NotificationKind.Point ) )
			.Select( row => row with
			{
				Hovered = row.Items.Any( item => item.Id == hovered ) ? hovered : null,
				IsExpanded = expanded.Contains( row.Key ),
			} )
			.ToList();
		var padding = IsPopover ? theme.Spacing * 2 : NotificationViews.ContentPadding;
		Blob view = rows.Count == 0
			? new Container
			{
				Key = "notifications-empty",
				FlexGrow = 1,
				Padding = new Edges( padding, padding ),
				Children = [NotificationViews.BuildColumn( "notifications-empty-body", [NotificationViews.BuildText( "No notifications yet. Chats that finish or need you while you are elsewhere, and GitHub updates, appear here.", muted: true )], 0 )],
			}
			: new Virtual
			{
				Key = "notifications-transcript",
				Handle = _follow.Handle,
				Source = new MeasuredRowsSource<NotificationRow>( rows, _buildRow, static _ => EstimatedRowHeight, _follow.IsFollowing ),
				AccessibilityName = "Notifications",
				FlexGrow = 1,
				MinHeight = 0,
				Padding = new Edges( padding, padding, padding, IsPopover ? 0 : padding ),
				OverflowX = Overflow.Hidden,
				ScrollbarY = ViewStyle.BuildScrollbar(),
			};
		var area = new List<Blob> { view };
		if ( rows.Count > 0 && !_follow.IsFollowing )
			area.Add( NotificationViews.BuildLatest( _follow.ScrollToEnd ) );

		var children = new List<Blob>();
		if ( IsPopover )
			children.Add( BuildPopoverHeader( unread ) );
		else
			children.Add( new Text
			{
				Key = "notifications-title",
				Content = unread > 0 ? $"Notifications · {unread} unread" : "Notifications",
				Padding = new Edges( theme.Spacing, padding, 0, padding ),
				FontSize = ViewStyle.SmallSize,
				Color = theme.Muted,
				TextAlign = TextAlign.Center,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		children.Add( new Container
		{
			Key = "notifications-area",
			FlexGrow = 1,
			MinHeight = 0,
			Children = area,
		} );
		if ( BuildDecisions() is { } decisions )
			children.Add( decisions );
		if ( !IsPopover )
			children.Add( new Row
			{
				Key = "notifications-actions",
				JustifyContent = JustifyContent.Center,
				Gap = theme.Spacing,
				Padding = new Edges( theme.Spacing, padding, padding, padding ),
				Children =
				[
					BuildQuietButton( "Mark all read", () => feed.MarkAllRead(), unread > 0 ),
					BuildQuietButton( "Clear all", feed.Clear, items.Count > 0 ),
				],
			} );

		return new Container
		{
			Key = "notifications",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Notification feed",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			FontFamily = ViewStyle.FontFamily,
			FontSize = theme.FontSize,
			Color = theme.Text,
			Children = children,
		};
	}

	/// <summary>Builds the questions agents are waiting on, each under its chat's name, answered here as in the chat; null when none waits.</summary>
	/// <remarks>It sits where the chat has its question, between the feed and its actions, so the answer is given without leaving the feed.</remarks>
	private Blob? BuildDecisions()
	{
		var prompts = NotificationPromptSource.ListAll( context.App );
		if ( prompts.Count == 0 )
			return null;

		var theme = Theme.Current;
		var padding = IsPopover ? theme.Spacing * 2 : NotificationViews.ContentPadding;
		var children = new List<Blob>();
		foreach ( var prompt in prompts )
		{
			var name = new List<Blob>
			{
				BuildGlyph( AgentGlyph ),
				new Text
				{
					Content = prompt.Title,
					FontWeight = SourceWeight,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Text { Content = "is waiting for you", Color = theme.Muted, FlexShrink = 0 },
			};

			// The name opens where the prompt comes from, for answers that need its context; another profile's prompt opens after switching to it.
			Blob header = prompt.Profile is null || prompt.Profile == context.App.Settings.ActiveProfile
				? BuildQuietLink( $"Open {prompt.Title}", name, () =>
				{
					dismiss?.Invoke();
					prompt.Open();
				} )
				: new Row { AlignItems = AlignItems.Center, Gap = theme.Spacing, MinWidth = 0, Children = name };
			children.Add( NotificationViews.BuildColumn( "decision:" + prompt.Key,
			[
				header,
				prompt.Build( "feed" ) ?? new Container(),
			], theme.Spacing ) );
		}

		return new Container
		{
			Key = "notifications-decisions",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Waiting for you",
			FlexShrink = 0,
			MaxHeight = IsPopover ? 340 : Length.Percent( 60 ),
			OverflowY = Overflow.Scroll,
			Gap = theme.Spacing * 2,
			Padding = new Edges( theme.Spacing * 1.5, padding ),
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Children = children,
		};
	}

	/// <summary>Builds a borderless row of content that runs an action, highlighted under the pointer.</summary>
	private Button BuildQuietLink( string label, List<Blob> children, Action action )
	{
		var theme = Theme.Current;
		return new Button
		{
			Key = "open",
			AlignSelf = AlignSelf.FlexStart,
			MaxWidth = Length.Percent( 100 ),
			MinWidth = 0,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 2, theme.Spacing / 2 ),
			Margin = new Edges( 0, 0, 0, -theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = label,
			OnClick = () => context.Run( action ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( action ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( action ) ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = children,
		};
	}

	/// <summary>Builds the popover's title line: its name, marking everything read, and the way to the full feed.</summary>
	private Row BuildPopoverHeader( int unread )
	{
		var theme = Theme.Current;
		var full = BuildQuietButton( "Open full feed", () =>
		{
			dismiss!();
			NotificationsModule.OpenFeed( context.App );
		} );
		full.Width = theme.ControlHeight;
		full.Padding = 0;
		full.JustifyContent = JustifyContent.Center;
		full.Children = [new Text { Content = FullFeedGlyph, FontFamily = NotificationViews.IconFont, FontSize = NotificationViews.IconSize, Color = theme.Muted }];
		return new Row
		{
			Key = "notifications-header",
			Height = HeaderHeight,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( 0, theme.Spacing, 0, theme.Spacing * 2 ),
			Children =
			[
				new Text
				{
					Content = unread > 0 ? $"Notifications · {unread} unread" : "Notifications",
					FontWeight = SourceWeight,
					FlexGrow = 1,
					FlexShrink = 1,
					MinWidth = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				BuildQuietButton( "Mark all read", () => context.App.Notifications.MarkAllRead(), unread > 0 ),
				full,
			],
		};
	}

	/// <summary>Builds a borderless muted text action, like the chat's own small controls.</summary>
	private Button BuildQuietButton( string label, Action action, bool isEnabled = true )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( label, () => context.Run( action ), isEnabled );
		button.BackgroundColor = Color.Transparent;
		button.BorderWidth = 0;
		button.Color = theme.Muted;
		button.FontSize = ViewStyle.SmallSize;
		button.HoverStyle = new Style { BackgroundColor = theme.Hover, Color = theme.Text };
		return button;
	}

	/// <summary>Lists the rows oldest first: a separator for each day, each notification worth reading, and each run of short updates.</summary>
	/// <param name="newestFirst">The notifications as the feed stores them, newest first.</param>
	/// <param name="now">The current time, which names today and yesterday.</param>
	/// <param name="kinds">The kinds modules contributed, which mark short updates and word their summaries.</param>
	internal static List<NotificationRow> ComputeRows( IReadOnlyList<LoknNotification> newestFirst, DateTimeOffset now, IReadOnlyList<NotificationKind> kinds )
	{
		var spacing = Theme.Current.Spacing;
		var rows = new List<NotificationRow>( newestFirst.Count + 4 );
		var run = new List<LoknNotification>();
		LoknNotification? previous = null;

		void Flush()
		{
			if ( run.Count == 0 )
				return;

			rows.Add( new( "run:" + run[0].Id.ToString( "N" ), NotificationRowKind.Activity, ComputeSummary( run, kinds ), [.. run], rows.Count == 0 || rows[^1].Kind == NotificationRowKind.Day ? spacing : spacing * 3 ) );
			run.Clear();
		}

		for ( var index = newestFirst.Count - 1; index >= 0; index-- )
		{
			var item = newestFirst[index];
			var day = item.Time.ToLocalTime().Date;
			if ( previous is null || previous.Time.ToLocalTime().Date != day )
			{
				Flush();
				rows.Add( new( "day:" + day.ToString( "yyyyMMdd", CultureInfo.InvariantCulture ), NotificationRowKind.Day,
					FormatDay( day, now.ToLocalTime().Date ), [], previous is null ? 0 : spacing * 4 ) );
			}

			if ( IsShort( item, kinds ) )
			{
				// Short updates from one source fold together like a chat's tool calls; another source or a long pause starts a new run.
				if ( run.Count > 0 && (item.Time - run[^1].Time > GroupWindow || ComputeGroup( item ) != ComputeGroup( run[0] )) )
					Flush();
				run.Add( item );
			}
			else
			{
				Flush();
				rows.Add( new( item.Id.ToString( "N" ), NotificationRowKind.Message, item.Title, [item], rows[^1].Kind == NotificationRowKind.Day ? spacing : spacing * 3 ) );
			}
			previous = item;
		}

		Flush();
		return rows;
	}

	/// <summary>Reports whether a notification is a short update rather than something to read: its kind says so, or its text is one short line; one with buttons is read in full.</summary>
	internal static bool IsShort( LoknNotification item, IReadOnlyList<NotificationKind> kinds ) => item.Actions is not { Count: > 0 }
		&& (FindKind( kinds, item )?.IsUpdate == true || (item.Message.Length <= ShortLimit && !item.Message.Contains( '\n' )));

	/// <summary>Summarizes a run of short updates by kind in order of first appearance, worded by each kind's module, such as "2 checks passed, Build fix finished".</summary>
	internal static string ComputeSummary( IReadOnlyList<LoknNotification> run, IReadOnlyList<NotificationKind> kinds )
	{
		var keys = new List<string>();
		var counts = new Dictionary<string, int>( StringComparer.Ordinal );
		var firsts = new Dictionary<string, LoknNotification>( StringComparer.Ordinal );
		foreach ( var item in run )
		{
			var key = ComputeKind( item );
			if ( !counts.ContainsKey( key ) )
			{
				keys.Add( key );
				firsts[key] = item;
			}
			counts[key] = counts.GetValueOrDefault( key ) + 1;
		}

		var phrases = keys.Select( key =>
		{
			var count = counts[key];
			var first = firsts[key];
			if ( first.Kind is { } kind && kinds.FirstOrDefault( candidate => candidate.Kind == kind )?.Summarize is { } summarize )
				return summarize( count, first );
			return key switch
			{
				"finished" => count == 1 ? $"{first.Title} finished" : $"{count} chats finished",
				"needs" => count == 1 ? $"{first.Title} needs you" : $"{count} chats need you",
				_ => count == 1 ? first.Message : $"{Count( count, "update", "updates" )} from {first.Title}",
			};
		} );
		var summary = string.Join( ", ", phrases );
		return summary.Length == 0 ? "Updates" : char.ToUpperInvariant( summary[0] ) + summary[1..];
	}

	/// <summary>Classifies a short update for its run's summary: by the kind it was posted with, else as a chat that finished or needs the user, else by source and title.</summary>
	private static string ComputeKind( LoknNotification item )
	{
		if ( item.Kind is { } kind )
			return kind;

		return ComputeSource( item, _ => null ) switch
		{
			NotificationSource.Agent when item.Message.StartsWith( "Finished", StringComparison.Ordinal ) => "finished",
			NotificationSource.Agent => "needs",
			var source => "other:" + source + ":" + item.Title,
		};
	}

	/// <summary>Gets what a run groups by: the posting module, else the title.</summary>
	private static string ComputeGroup( LoknNotification item ) => item.Source?.Split( '.' )[0] ?? item.Title;

	/// <summary>Finds the kind a module contributed for a notification: by its kind, else its source, else its source's module.</summary>
	internal static NotificationKind? FindKind( IReadOnlyList<NotificationKind> kinds, LoknNotification item )
	{
		var module = item.Source?.Split( '.' )[0];
		foreach ( var key in new[] { item.Kind, item.Source, module } )
		{
			if ( key is not null && kinds.FirstOrDefault( kind => kind.Kind == key ) is { } found )
				return found;
		}
		return null;
	}

	/// <summary>Formats a count with the singular or plural noun.</summary>
	private static string Count( int count, string one, string many ) => $"{count} {(count == 1 ? one : many)}";

	/// <summary>Finds where a notification came from: an agent's chat, the browser, or anything else.</summary>
	/// <remarks>Notifications saved before sources were kept fall back to their panel, while it is open.</remarks>
	internal static NotificationSource ComputeSource( LoknNotification item, Func<Guid, string?> panelType )
	{
		var source = item.Source ?? (item.PanelId is { } panelId ? panelType( panelId ) : null) ?? "";
		return source.Split( '.' )[0] switch
		{
			"conversations" => NotificationSource.Agent,
			"browser" => NotificationSource.Browser,
			_ => NotificationSource.Other,
		};
	}

	/// <summary>Names a day of notifications: Today, Yesterday, a weekday within the week, then the date.</summary>
	internal static string FormatDay( DateTime day, DateTime today ) => (today - day.Date).Days switch
	{
		0 => "Today",
		1 => "Yesterday",
		> 1 and < 7 => day.ToString( "dddd", CultureInfo.CurrentCulture ),
		_ => day.ToString( day.Year == today.Year ? "MMMM d" : "MMMM d, yyyy", CultureInfo.CurrentCulture ),
	};

	/// <summary>Builds one row in the reading column, below its gap.</summary>
	private Blob BuildRow( NotificationRow row ) => new Container
	{
		Width = Length.Percent( 100 ),
		Padding = new Edges( row.Gap, 0, 0, 0 ),
		Children = [NotificationViews.BuildColumn( "row", [row.Kind switch
		{
			NotificationRowKind.Day => BuildDay( row.Label ),
			NotificationRowKind.Message => BuildMessage( row ),
			_ => BuildActivity( row ),
		}], 0 )],
	};

	/// <summary>Builds the centered day name between two days of notifications.</summary>
	private static Text BuildDay( string label ) => new()
	{
		Content = label,
		FontSize = ViewStyle.SmallSize,
		Color = Theme.Current.Muted,
		TextAlign = TextAlign.Center,
	};

	/// <summary>Lays a line out after the time margin, where the time of the notification under the pointer shows, with its remove action at the right end.</summary>
	/// <param name="item">The notification whose time shows and whose hover reveals the line's actions.</param>
	/// <param name="isHovered">Whether the pointer is on the line.</param>
	/// <param name="content">The line's content.</param>
	/// <param name="removing">The notifications the remove action removes: the line's own, or a whole run for its summary.</param>
	private Row BuildTimed( LoknNotification item, bool isHovered, Blob content, IReadOnlyList<LoknNotification>? removing = null )
	{
		var theme = Theme.Current;
		removing ??= [item];
		var remove = theme.BuildButton( "Remove", () => context.Run( () =>
		{
			foreach ( var removed in removing )
				context.App.Notifications.Remove( removed.Id );
		} ) );
		remove.AccessibilityName = removing.Count == 1 ? $"Remove {item.Title}: {item.Message}" : $"Remove these {removing.Count} updates";
		remove.Width = theme.ControlHeight;
		remove.Height = theme.ControlHeight;
		remove.Padding = 0;
		remove.FlexShrink = 0;
		remove.BackgroundColor = Color.Transparent;
		remove.BorderWidth = 0;
		remove.Opacity = isHovered ? 1 : 0;
		remove.HoverStyle = new Style { Opacity = 1, BackgroundColor = theme.Hover };
		remove.FocusStyle = new Style { Opacity = 1, BackgroundColor = theme.Hover };
		remove.Children = [new Text { Content = RemoveGlyph, FontFamily = NotificationViews.IconFont, FontSize = NotificationViews.IconSize, Color = theme.Muted }];
		return new Row
		{
			Key = item.Id.ToString( "N" ),
			AlignItems = AlignItems.FlexStart,
			MinWidth = 0,
			OnPointerEnter = _ => UpdateHovered( item.Id ),
			OnPointerLeave = _ =>
			{
				if ( _hovered.Value == item.Id )
					UpdateHovered( Guid.Empty );
			},
			Children =
			[
				new Text
				{
					Content = item.Time.ToLocalTime().ToString( "t", CultureInfo.CurrentCulture ),
					Width = TimeGutter,
					FlexShrink = 0,
					Padding = new Edges( 3, theme.Spacing, 0, 0 ),
					FontSize = ViewStyle.SmallSize,
					TextAlign = TextAlign.Right,
					TextWrap = TextWrap.NoWrap,
					Color = isHovered ? theme.Muted : Color.Transparent,
				},
				content,
				new Spacer(),
				remove,
			],
		};
	}

	/// <summary>Marks the notification under the pointer, rebuilding only when it changes.</summary>
	private void UpdateHovered( Guid id )
	{
		if ( _hovered.Value != id )
			_hovered.Value = id;
	}

	/// <summary>Builds a notification worth reading like an agent's reply: its source's icon and name, which open it, then its text.</summary>
	private Row BuildMessage( NotificationRow row )
	{
		var item = row.Items[0];
		var theme = Theme.Current;
		var header = BuildOpen( item, [BuildSourceIcon( item ), new Text
		{
			Content = item.Title,
			FontWeight = SourceWeight,
			Color = item.IsRead ? theme.Muted : theme.Text,
			FlexShrink = 1,
			MinWidth = 0,
			TextWrap = TextWrap.NoWrap,
			TextTrimming = TextTrimming.Ellipsis,
		}, BuildUnread( item )] );
		return BuildTimed( item, row.Hovered == item.Id, new Container
		{
			FlexGrow = 1,
			FlexShrink = 1,
			MinWidth = 0,
			Gap = theme.Spacing / 2,
			Children =
			[
				header,
				new Container
				{
					Padding = new Edges( 0, 0, 0, SourceIconSize + theme.Spacing + theme.Spacing / 2 ),
					Gap = theme.Spacing,
					Children = [BuildBody( item.Message ), .. BuildActions( item, OpenLink ) is { } actions ? [actions] : Array.Empty<Blob>()],
				},
			],
		} );
	}

	/// <summary>Builds a notification's text as Markdown with the Markdown module's renderer, such as an agent's closing message with lists and code; plain text without the module.</summary>
	/// <remarks>The renderer is looked up on each build, so loading or reloading the Markdown module redraws the feed with it.</remarks>
	private Blob BuildBody( string message ) =>
		context.App.Services.Get<IMarkdownRenderer>() is { } markdown ? markdown.Build( message, OpenLink ) : NotificationViews.BuildText( message );

	/// <summary>Builds a run of short updates: one update as a line that opens it, or more as a summary that opens onto each.</summary>
	private Blob BuildActivity( NotificationRow row )
	{
		var theme = Theme.Current;
		if ( row.Items is [var only] )
			return BuildTimed( only, row.Hovered == only.Id, BuildOpen( only, [BuildSourceIcon( only ), BuildLine( row.Label, only.IsRead ), BuildUnread( only )] ) );

		var last = row.Items[^1];
		var sources = row.Items.Select( item => ComputeSource( item, ComputePanelType ) ).Distinct().ToArray();
		var toggle = new Button
		{
			Key = "toggle",
			FlexShrink = 1,
			MinWidth = 0,
			AlignSelf = AlignSelf.FlexStart,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 2, theme.Spacing / 2 ),
			Margin = new Edges( 0, 0, 0, -theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = row.Label,
			OnClick = () => context.Run( () => UpdateExpanded( row.Key ) ),
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, () => context.Run( () => UpdateExpanded( row.Key ) ) ),
				new( Key.Space, KeyModifiers.None, () => context.Run( () => UpdateExpanded( row.Key ) ) ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children =
			[
				sources is [var source] ? BuildSourceIcon( source ) : BuildGlyph( BellGlyph ),
				BuildLine( row.Label, row.Items.All( item => item.IsRead ) ),
				theme.BuildChevron( row.IsExpanded ),
				.. row.Items.Any( item => !item.IsRead ) ? [BuildDot()] : Array.Empty<Blob>(),
			],
		};

		var head = BuildTimed( last, row.Hovered == last.Id && !row.IsExpanded, toggle, row.Items );
		head.Key = "head";
		var children = new List<Blob> { head };
		if ( row.IsExpanded )
		{
			foreach ( var item in row.Items )
			{
				var line = BuildOpen( item, [BuildSourceIcon( item ), BuildLine( ComputeSource( item, ComputePanelType ) == NotificationSource.Agent ? $"{item.Title} · {item.Message}" : item.Message, item.IsRead ), BuildUnread( item )] );
				line.Margin = new Edges( 0, 0, 0, theme.Spacing * 2 - theme.Spacing / 2 );
				children.Add( BuildTimed( item, row.Hovered == item.Id, line ) );
			}
		}

		return new Container
		{
			Key = row.Key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = row.Label,
			Gap = theme.Spacing / 2,
			Children = children,
		};
	}

	/// <summary>Opens or closes a run of short updates.</summary>
	private void UpdateExpanded( string key )
	{
		var expanded = new HashSet<string>( _expanded.Value );
		if ( !expanded.Remove( key ) )
			expanded.Add( key );
		_expanded.Value = expanded;
	}

	/// <summary>Builds a row of content that opens a notification when clicked, highlighted under the pointer.</summary>
	private Button BuildOpen( LoknNotification item, Blob[] children )
	{
		var theme = Theme.Current;
		void Open() => context.Run( () =>
		{
			dismiss?.Invoke();
			NotificationsModule.Open( context.App, item, IsPopover ? null : context.Id );
		} );
		return new Button
		{
			Key = "open",
			FlexShrink = 1,
			MinWidth = 0,
			AlignSelf = AlignSelf.FlexStart,
			MaxWidth = Length.Percent( 100 ),
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			Padding = new Edges( 2, theme.Spacing / 2 ),
			Margin = new Edges( 0, 0, 0, -theme.Spacing / 2 ),
			BorderRadius = theme.Radius,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = $"{(item.IsRead ? "" : "Unread: ")}{item.Title}, {item.Message}",
			OnClick = Open,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Open ),
				new( Key.Space, KeyModifiers.None, Open ),
			],
			HoverStyle = new Style { BackgroundColor = theme.Hover },
			FocusStyle = new Style { BackgroundColor = theme.Hover },
			Children = children,
		};
	}

	/// <summary>Builds one line of muted update text, brighter while unread.</summary>
	private static Text BuildLine( string text, bool isRead ) => new()
	{
		Content = text,
		Color = isRead ? Theme.Current.Muted : Theme.Current.Text,
		FlexShrink = 1,
		MinWidth = 0,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds the unread dot, or nothing for a read notification.</summary>
	private static Blob BuildUnread( LoknNotification item ) => item.IsRead ? new Container { Width = 0 } : BuildDot();

	/// <summary>Builds the dot that marks unread notifications.</summary>
	private static Container BuildDot() => new()
	{
		Width = UnreadDotSize,
		Height = UnreadDotSize,
		FlexShrink = 0,
		BorderRadius = UnreadDotSize / 2.0,
		BackgroundColor = Theme.Current.Attention,
	};

	/// <summary>Builds a notification's icon: the one a module contributed for its kind or source, else its source's built-in icon.</summary>
	private Blob BuildSourceIcon( LoknNotification item ) => BuildSourceIcon( context.App, item, ComputePanelType );

	/// <summary>Builds a notification's icon: the one a module contributed for its kind or source, as a vector or a glyph, else its source's built-in icon.</summary>
	internal static Blob BuildSourceIcon( LoknApplication app, LoknNotification item, Func<Guid, string?> panelType ) =>
		FindKind( app.Extensions.Get<NotificationKind>( NotificationKind.Point ), item ) switch
		{
			{ Icon: { } icon } => new Shape
			{
				Path = icon,
				Fit = ShapeFit.Contain,
				Width = SourceIconSize,
				Height = SourceIconSize,
				FlexShrink = 0,
				Fill = new VectorPaint( Theme.Current.Text ),
			},
			{ } kind => BuildGlyph( kind.Glyph ),
			null => BuildSourceIcon( ComputeSource( item, panelType ) ),
		};

	/// <summary>Builds a source's built-in icon: a glyph for agents, the browser and the rest.</summary>
	internal static Blob BuildSourceIcon( NotificationSource source ) => source switch
	{
		NotificationSource.Agent => BuildGlyph( AgentGlyph ),
		NotificationSource.Browser => BuildGlyph( BrowserGlyph ),
		_ => BuildGlyph( BellGlyph ),
	};

	/// <summary>Builds a notification's buttons, each following its link; null when it has none.</summary>
	/// <param name="item">The notification.</param>
	/// <param name="open">Follows a link where the view opens links.</param>
	internal static Row? BuildActions( LoknNotification item, Action<Uri> open ) => item.Actions is { Count: > 0 } actions
		? new Row
		{
			Key = "actions",
			Gap = Theme.Current.Spacing,
			FlexWrap = FlexWrap.Wrap,
			Children = [.. actions.Select( action => (Blob)Theme.Current.BuildButton( action.Label, () => open( action.Link ) ) )],
		}
		: null;

	/// <summary>Builds one icon glyph at the source icon size.</summary>
	private static Text BuildGlyph( string glyph ) => new()
	{
		Content = glyph,
		FontFamily = NotificationViews.IconFont,
		FontSize = SourceIconSize,
		Width = SourceIconSize,
		FlexShrink = 0,
		Color = Theme.Current.Muted,
	};

	/// <summary>Follows a link in a notification's text, beside the feed's panel or, from the popover, wherever links open.</summary>
	private void OpenLink( Uri uri ) => context.Run( () =>
	{
		dismiss?.Invoke();
		context.App.OpenLink( uri, IsPopover ? null : context.Id );
	} );

	/// <summary>Gets the type of an open panel, or null when it has closed.</summary>
	private string? ComputePanelType( Guid panelId ) => context.App.Panels.Panels.FirstOrDefault( panel => panel.Id == panelId )?.TypeId;
}
