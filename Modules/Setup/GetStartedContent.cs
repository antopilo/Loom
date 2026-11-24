using Goo;
using Lokn.Core;

namespace Lokn.Modules.Setup;

/// <summary>Lists what Loom needs from the machine, each with its state and a one-click fix, and offers to start a chat once the required ones are ready.</summary>
/// <remarks>
/// Items come from <see cref="SetupItem.Point"/> in their order. Checks and fixes run off the UI thread and stop when the panel closes; one fix
/// runs at a time, and every item is checked again after it, because one fix can satisfy another. Items a module load or reload adds are checked
/// when they appear.
/// </remarks>
internal sealed class GetStartedContent : PanelContent
{
	/// <summary>Defines page padding in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines the widest the page grows, in logical pixels.</summary>
	private const int PageWidth = 720;

	/// <summary>Defines the heading size in logical pixels.</summary>
	private const int HeadingSize = 22;

	/// <summary>Defines the item title weight.</summary>
	private const int TitleWeight = 600;

	/// <summary>Defines metadata text size in logical pixels.</summary>
	private const int SmallSize = 12;

	/// <summary>Defines the status dot diameter in logical pixels.</summary>
	private const int DotSize = 7;

	/// <summary>Defines the command that starts a new chat, registered by Conversations.</summary>
	private const string ChatCommand = "conversations.new";

	/// <summary>Stores the panel's context.</summary>
	private readonly PanelContext _context;

	/// <summary>Retains the last status of each item; an item without one has not been checked yet.</summary>
	private readonly State<IReadOnlyDictionary<SetupItem, SetupStatus>> _statuses;

	/// <summary>Retains the items whose check is running.</summary>
	private readonly State<IReadOnlySet<SetupItem>> _checking = new( new HashSet<SetupItem>() );

	/// <summary>Retains the key of the item whose fix is running, or empty.</summary>
	private readonly State<string> _fixing = new( "" );

	/// <summary>Retains the last progress line of the running fix, or empty.</summary>
	private readonly State<string> _progress = new( "" );

	/// <summary>Retains the key of the item whose last fix failed and its message, or empty ones.</summary>
	private readonly State<(string Key, string Message)> _failure = new( ("", "") );

	/// <summary>Stores the latest check run started for each item, so an older run's result is dropped.</summary>
	private readonly Dictionary<SetupItem, int> _runs = [];

	/// <summary>Stores the number of the last check run started.</summary>
	private int _lastRun;

	/// <summary>Reports whether a check of newly contributed items is posted and not yet started.</summary>
	private bool _isCheckQueued;

	/// <summary>Creates the page with the statuses the startup check found, when it opened the page, and checks every item it has none for.</summary>
	internal GetStartedContent( PanelContext context )
	{
		_context = context;
		_statuses = new( context.Input as IReadOnlyDictionary<SetupItem, SetupStatus> ?? new Dictionary<SetupItem, SetupStatus>() );
		Check( SetupModule.GetItems( context.App ).Where( item => !_statuses.Value.ContainsKey( item ) ) );
	}

	/// <summary>Builds the heading, a card per item, and the ready line once every required item is ready.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		// Reading the modules rebuilds the page when one loads, reloads or unloads, which changes the contributed items.
		_ = _context.App.Modules;
		var items = SetupModule.GetItems( _context.App );
		var statuses = _statuses.Value;
		var checking = _checking.Value;
		QueueUnchecked( items, statuses, checking );

		var page = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Children =
				[
					new Text { Content = "Get started", FontSize = HeadingSize, FlexGrow = 1 },
					BuildAction( checking.Count > 0 ? "Checking…" : "Check again", CheckAll, checking.Count == 0 && _fixing.Value.Length == 0 ),
				],
			},
			new Text { Content = "Loom checks what it needs and installs it for you.", Color = theme.Muted },
		};
		foreach ( var item in items )
			page.Add( BuildItem( item, statuses.GetValueOrDefault( item ), checking.Contains( item ) ) );
		if ( items.Where( item => item.IsRequired ).All( item => !checking.Contains( item ) && statuses.GetValueOrDefault( item ) is { State: SetupState.Ready } ) )
			page.Add( BuildReady() );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Get started",
			FlexGrow = 1,
			MinHeight = 0,
			Children =
			[
				new Container
				{
					FlexGrow = 1,
					MinHeight = 0,
					Padding = ContentPadding,
					OverflowY = Overflow.Scroll,
					ScrollbarY = ViewStyle.BuildScrollbar(),
					AlignItems = AlignItems.Center,
					Children =
					[
						new Container
						{
							Width = Length.Percent( 100 ),
							MaxWidth = PageWidth,
							FlexShrink = 0,
							Gap = theme.Spacing * 2,
							Children = page,
						},
					],
				},
			],
		};
	}

	/// <summary>Builds an item's card: its state, title and tag with the fix on the right, then its purpose, detail, and the running fix's progress or the last fix's failure.</summary>
	/// <param name="item">The item.</param>
	/// <param name="status">Its last status, or null before its first check finishes.</param>
	/// <param name="isChecking">Whether its check is running.</param>
	private Blob BuildItem( SetupItem item, SetupStatus? status, bool isChecking )
	{
		var theme = Theme.Current;
		var isFixing = _fixing.Value == item.Key;
		var head = new List<Blob>();
		if ( !isChecking && status is not null )
			head.Add( BuildDot( ComputeColor( item, status.State ) ) );
		head.Add( new Text { Content = item.Title, FontWeight = TitleWeight, Color = theme.Text, MinWidth = 0 } );
		head.Add( BuildTag( item.IsRequired ? "Required" : "Optional" ) );
		head.Add( new Container { FlexGrow = 1 } );
		if ( status is { State: not SetupState.Ready } && item.FixAsync is not null )
		{
			var label = item.FixLabel ?? "Fix";
			var canFix = _fixing.Value.Length == 0 && !isChecking && status.State != SetupState.Blocked;
			head.Add( BuildAction( isFixing ? label + "…" : label, () => _ = FixAsync( item ), canFix ) );
		}

		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				MinHeight = theme.ControlHeight,
				Children = head,
			},
			new Text { Content = item.Purpose, FontSize = SmallSize, Color = theme.Muted },
			new Text { Content = isChecking || status is null ? "Checking…" : status.Detail, FontSize = SmallSize, Color = theme.Muted },
		};
		if ( isFixing && _progress.Value.Length > 0 )
		{
			children.Add( new Text
			{
				Content = _progress.Value,
				FontSize = SmallSize,
				Color = theme.Accent,
				MinWidth = 0,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			} );
		}
		else if ( _failure.Value is { Key.Length: > 0 } failure && failure.Key == item.Key && status is not { State: SetupState.Ready } )
			children.Add( new Text { Content = failure.Message, FontSize = SmallSize, Color = theme.Error } );

		return new Container
		{
			Key = item.Key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = item.Title,
			Gap = theme.Spacing / 2,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
			BackgroundColor = theme.Raised,
			BorderRadius = theme.Radius * 2,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			Children = children,
		};
	}

	/// <summary>Builds the line shown once every required item is ready, with the button that starts a chat, or closes the page without Conversations.</summary>
	private Row BuildReady()
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "ready",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing,
			MinHeight = theme.ControlHeight,
			Children =
			[
				BuildDot( theme.Success ),
				new Text { Content = "Loom is ready.", Color = theme.Text, FlexGrow = 1 },
				BuildAction( _context.App.Commands.CanExecute( ChatCommand ) ? "Start a chat" : "Close", StartChat ),
			],
		};
	}

	/// <summary>Builds the small Required or Optional tag beside an item's title.</summary>
	private static Container BuildTag( string label ) => new()
	{
		FlexShrink = 0,
		Padding = new Edges( 0, Theme.Current.Spacing / 2 ),
		BorderRadius = Theme.Current.Radius,
		BorderWidth = Theme.Current.BorderWidth,
		BorderColor = Theme.Current.Border,
		Children = [new Text { Content = label, FontSize = SmallSize, Color = Theme.Current.Muted }],
	};

	/// <summary>Builds a small status dot.</summary>
	private static Container BuildDot( Color color ) => new()
	{
		Width = DotSize,
		Height = DotSize,
		FlexShrink = 0,
		BorderRadius = DotSize / 2.0,
		BackgroundColor = color,
	};

	/// <summary>Builds a compact action; a disabled one stays visible but muted.</summary>
	private Button BuildAction( string label, Action action, bool isEnabled = true )
	{
		var button = Theme.Current.BuildButton( label, () => _context.Run( action ), isEnabled );
		button.FlexShrink = 0;
		return button;
	}

	/// <summary>Gets an item's dot color: ready, missing (a warning when optional), or blocked behind another item.</summary>
	private static Color ComputeColor( SetupItem item, SetupState state ) => state switch
	{
		SetupState.Ready => Theme.Current.Success,
		SetupState.Missing => item.IsRequired ? Theme.Current.Error : Theme.Current.AnsiYellow,
		_ => Theme.Current.Inactive,
	};

	/// <summary>Checks every contributed item again and forgets the statuses of items no longer contributed.</summary>
	private void CheckAll()
	{
		var items = SetupModule.GetItems( _context.App );
		_statuses.Value = _statuses.Value.Where( pair => items.Contains( pair.Key ) ).ToDictionary();
		Check( items );
	}

	/// <summary>Posts a check of the items a module load or reload contributed since the last check, unless a fix runs and checks everything after it.</summary>
	private void QueueUnchecked( IReadOnlyList<SetupItem> items, IReadOnlyDictionary<SetupItem, SetupStatus> statuses, IReadOnlySet<SetupItem> checking )
	{
		if ( _isCheckQueued || _fixing.Value.Length > 0 || items.All( item => statuses.ContainsKey( item ) || checking.Contains( item ) ) )
			return;

		// A build must not change the state it reads, so the check starts after it.
		_isCheckQueued = _context.Post( () =>
		{
			_isCheckQueued = false;
			Check( SetupModule.GetItems( _context.App ).Where( item => !_statuses.Value.ContainsKey( item ) && !_checking.Value.Contains( item ) ) );
		} );
	}

	/// <summary>Starts a check of each item off the UI thread; each shows its result as soon as it finishes.</summary>
	private void Check( IEnumerable<SetupItem> items )
	{
		var checking = new HashSet<SetupItem>( _checking.Value );
		foreach ( var item in items )
		{
			var run = ++_lastRun;
			_runs[item] = run;
			checking.Add( item );
			_ = CheckAsync( item, run );
		}
		_checking.Value = checking;
	}

	/// <summary>Runs one item's check and shows its status unless a newer check of the item started meanwhile.</summary>
	private async Task CheckAsync( SetupItem item, int run )
	{
		SetupStatus status;
		try
		{
			status = await SetupModule.CheckAsync( item, _context.Lifetime );
		}
		catch ( OperationCanceledException )
		{
			// The panel closed.
			return;
		}

		_context.Post( () =>
		{
			if ( _runs.GetValueOrDefault( item ) != run )
				return;

			_runs.Remove( item );
			_statuses.Value = new Dictionary<SetupItem, SetupStatus>( _statuses.Value ) { [item] = status };
			var checking = new HashSet<SetupItem>( _checking.Value );
			checking.Remove( item );
			_checking.Value = checking;
		} );
	}

	/// <summary>Runs an item's fix off the UI thread, showing its last progress line and any failure, then checks every item again.</summary>
	private async Task FixAsync( SetupItem item )
	{
		if ( _fixing.Value.Length > 0 || item.FixAsync is not { } fix )
			return;

		_fixing.Value = item.Key;
		_progress.Value = "";
		_failure.Value = ("", "");
		try
		{
			// A late line must not bring back the progress text once the fix has finished.
			var progress = new Progress<string>( line => _context.Post( () => _progress.Value = _fixing.Value.Length == 0 ? "" : line.Trim() ) );
			await Task.Run( () => fix( progress, _context.Lifetime ) );
		}
		catch ( OperationCanceledException ) when ( _context.Lifetime.IsCancellationRequested )
		{
			// The panel closed.
			return;
		}
		catch ( Exception error )
		{
			_context.Post( () => _failure.Value = (item.Key, error.Message) );
		}

		_context.Post( () =>
		{
			_fixing.Value = "";
			_progress.Value = "";
			CheckAll();
		} );
	}

	/// <summary>Closes the page, then starts a chat when Conversations is loaded.</summary>
	/// <remarks>The page closes first: a new chat opens in a workspace of its own, whose layout should not keep it.</remarks>
	private void StartChat()
	{
		var app = _context.App;
		app.Panels.Close( _context.Id );
		if ( app.Commands.CanExecute( ChatCommand ) )
			_ = app.Commands.ExecuteAsync( ChatCommand );
	}
}
