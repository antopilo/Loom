using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Shows how much of each provider rate limit the active profile's accounts have spent, and when each limit resets.</summary>
/// <remarks>Reads through <see cref="OmpService.GetUsageAsync"/> when opened and every <see cref="RefreshSeconds"/> seconds while open.</remarks>
public sealed class OmpUsageContent : PanelContent
{
	/// <summary>Defines the panel content inset in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines the panel heading size in logical pixels.</summary>
	private const int HeadingSize = 22;

	/// <summary>Defines the width of a limit's name column in logical pixels, so the bars line up.</summary>
	private const int LabelWidth = 170;

	/// <summary>Defines the width of a limit's bar in logical pixels.</summary>
	private const int BarWidth = 220;

	/// <summary>Defines the height of a limit's bar in logical pixels.</summary>
	private const int BarHeight = 8;

	/// <summary>Defines the seconds between refreshes while the panel is open; Omp caches provider answers, so this costs little.</summary>
	private const int RefreshSeconds = 60;

	/// <summary>Stores the panel's guarded dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Reads usage through Omp.</summary>
	private readonly OmpService _service;

	/// <summary>Schedules the next refresh after the previous one finishes.</summary>
	private readonly Timer _timer;

	/// <summary>Tracks whether a refresh is running; it changes around every refresh, which redraws the panel.</summary>
	private readonly State<bool> _isBusy = new( false );

	/// <summary>Reports the last error, or empty.</summary>
	private readonly State<string> _status = new( "" );

	/// <summary>Stores the last usage read, or null before the first; Goo's state holds no nulls, so this is a plain field.</summary>
	private OmpUsageReport? _report;

	/// <summary>Stores the profile the last usage was read for.</summary>
	private SettingsProfile _profile;

	/// <summary>Creates the panel and starts its first refresh.</summary>
	internal OmpUsageContent( PanelContext context, OmpService service )
	{
		_context = context;
		_service = service;
		_profile = context.App.Settings.ActiveProfile;
		_timer = new( _ => _context.Post( UpdateRefresh ), null, Timeout.Infinite, Timeout.Infinite );
		UpdateRefresh();
	}

	/// <summary>Stops the refresh timer, which would otherwise keep running Omp for a closed panel.</summary>
	public override void Closed() => _timer.Dispose();

	/// <summary>Builds the heading, then each account with one bar per limit.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var isBusy = _isBusy.Value;
		var now = DateTimeOffset.Now;
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing * 2,
				Children =
				[
					new Text { Content = "Usage", FontSize = HeadingSize, },
					theme.BuildButton( isBusy ? "Refreshing…" : "Refresh", () => _context.Run( UpdateRefresh ), isEnabled: !isBusy ),
				],
			},
			new Text
			{
				Content = _report is { } read ? $"{_profile} profile · updated {read.FetchedAt.ToLocalTime():t}" : $"{_profile} profile",
				Color = theme.Muted,
			},
		};

		if ( _status.Value.Length > 0 )
			children.Add( new Text
			{
				Content = _status.Value,
				Color = theme.Error,
				Selectable = true,
			} );

		if ( _report is { Accounts.Count: 0 } )
			children.Add( new Text { Content = "No signed-in account of this profile reports usage limits.", Color = theme.Muted, } );

		foreach ( var account in _report?.Accounts ?? [] )
			children.Add( BuildAccount( account, now ) );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Omp usage",
			FlexGrow = 1,
			MinHeight = 0,
			Padding = ContentPadding,
			Gap = theme.Spacing * 1.5,
			OverflowY = Overflow.Scroll,
			Children = children,
		};
	}

	/// <summary>Builds one account: its provider and name, its limits, and its saved resets when it has any.</summary>
	public static Container BuildAccount( OmpUsageAccount account, DateTimeOffset now )
	{
		var theme = Theme.Current;
		var provider = account.Provider.Length > 0 ? char.ToUpperInvariant( account.Provider[0] ) + account.Provider[1..] : "Provider";
		var children = new List<Blob>
		{
			new Text
			{
				Content = account.Account.Length > 0 ? $"{provider} · {account.Account}" : provider,
				FontWeight = 600,
				Selectable = true,
			},
		};
		children.AddRange( account.Limits.Select( limit => BuildLimit( limit, now ) ) );

		if ( account.SavedResets > 0 )
			children.Add( new Text
			{
				Content = $"{account.SavedResets} saved limit {( account.SavedResets == 1 ? "reset" : "resets" )}, {account.UsableResets} usable now. Spend one with `omp usage reset` in a terminal.",
				Color = theme.Muted,
				Selectable = true,
			} );

		return new Container
		{
			Gap = theme.Spacing,
			Children = children,
		};
	}

	/// <summary>Builds one limit as its name, a bar filled to the spent share, and the share with the time left.</summary>
	private static Row BuildLimit( OmpUsageLimit limit, DateTimeOffset now )
	{
		var theme = Theme.Current;
		var color = limit.IsExhausted ? theme.Error : theme.Accent;
		var reset = FormatReset( limit.ResetsAt, now );
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 1.5,
			Children =
			[
				new Text
				{
					Content = limit.Label,
					Width = LabelWidth,
					FlexShrink = 0,
					TextWrap = TextWrap.NoWrap,
					TextTrimming = TextTrimming.Ellipsis,
				},
				new Container
				{
					Width = BarWidth,
					Height = BarHeight,
					FlexShrink = 1,
					BackgroundColor = theme.Raised,
					BorderRadius = BarHeight / 2,
					Overflow = Overflow.Hidden,
					Accessibility = new Accessibility { Hidden = true },
					Children =
					[
						new Container
						{
							Width = Length.Percent( limit.UsedFraction * 100 ),
							Height = Length.Percent( 100 ),
							BackgroundColor = color,
						},
					],
				},
				new Text
				{
					Content = reset.Length > 0 ? $"{limit.UsedFraction:P0} used · {reset}" : $"{limit.UsedFraction:P0} used",
					Color = limit.IsExhausted ? theme.Error : theme.Muted,
					Selectable = true,
				},
			],
		};
	}

	/// <summary>Formats when a limit resets, as the time left and the local clock time, or empty when unknown.</summary>
	/// <param name="resetsAt">When the limit's window starts over.</param>
	/// <param name="now">The current time the wait counts from.</param>
	internal static string FormatReset( DateTimeOffset? resetsAt, DateTimeOffset now )
	{
		if ( resetsAt is not { } time )
			return "";

		var left = time - now;
		if ( left <= TimeSpan.Zero )
			return "resets now";

		// A reset later than today names its day, since a bare clock time would read as today's.
		var local = time.ToLocalTime();
		var clock = local.Date == now.ToLocalTime().Date ? $"{local:t}" : $"{local:ddd} {local:t}";
		return $"resets in {FormatDuration( left )} ({clock})";
	}

	/// <summary>Formats a wait in its two largest units, such as "1d 5h", "2h 10m" or "20m".</summary>
	internal static string FormatDuration( TimeSpan duration )
	{
		if ( duration.TotalDays >= 1 )
			return $"{(int)duration.TotalDays}d {duration.Hours}h";
		if ( duration.TotalHours >= 1 )
			return $"{(int)duration.TotalHours}h {duration.Minutes}m";

		return $"{Math.Max( 1, (int)Math.Ceiling( duration.TotalMinutes ) )}m";
	}

	/// <summary>Reads the active profile's usage off the UI thread, then schedules the next refresh.</summary>
	private void UpdateRefresh()
	{
		if ( _isBusy.Value )
			return;

		_isBusy.Value = true;
		_ = UpdateUsageAsync( _context.App.Settings.ActiveProfile );
	}

	/// <summary>Runs one refresh and applies its report or its error on the UI thread.</summary>
	private async Task UpdateUsageAsync( SettingsProfile profile )
	{
		OmpUsageReport? report = null;
		var status = "";
		try
		{
			report = await _service.GetUsageAsync( profile, _context.Lifetime ).ConfigureAwait( false );
		}
		catch ( Exception error )
		{
			status = error is OperationCanceledException ? "" : error.Message;
		}

		_context.Post( () =>
		{
			// A failed refresh keeps the last report on screen under its error.
			if ( report is not null )
			{
				_report = report;
				_profile = profile;
			}

			_status.Value = status;
			_isBusy.Value = false;
			_timer.Change( TimeSpan.FromSeconds( RefreshSeconds ), Timeout.InfiniteTimeSpan );
		} );
	}
}
