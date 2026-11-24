using System.Globalization;
using Goo;
using Lokn.Core;

namespace Lokn;

/// <summary>Edits registered settings in global defaults, Work, or Personal without duplicating inherited values, one tab per module that registers settings.</summary>
/// <remarks>
/// Each category is a card of rows: the title and description on the left, and on the right the widget the setting's kind asks for: a switch
/// for a Boolean, a stepper for an integer, a field for text, or segments under the text for a setting with <see cref="SettingDefinition.Choices"/>.
/// </remarks>
internal sealed class SettingsContent( PanelContext context ) : PanelContent
{
	/// <summary>Defines the settings content inset in logical pixels.</summary>
	private const int ContentPadding = 24;

	/// <summary>Defines the right inset that keeps the heading clear of the window's close button and holds the scrollbar beside the settings, in logical pixels.</summary>
	private const int EdgeGutter = 44;

	/// <summary>Defines the widest the settings column grows, in logical pixels.</summary>
	private const int ColumnWidth = 720;

	/// <summary>Defines the settings heading size in logical pixels.</summary>
	private const int HeadingSize = 22;

	/// <summary>Defines the height of fields and steppers in logical pixels.</summary>
	private const int EditorHeight = 32;

	/// <summary>Defines the text field's width in logical pixels.</summary>
	private const int FieldWidth = 240;

	/// <summary>Defines the stepper value's width in logical pixels.</summary>
	private const int StepperValueWidth = 72;

	/// <summary>Defines the emphasized weight of setting titles.</summary>
	private const int TitleWeight = 600;

	/// <summary>Defines the narrowest a setting's title and description get before its widget moves under them, in logical pixels.</summary>
	private const int TextMinWidth = 260;

	/// <summary>Tracks the scope currently being edited independently of the active profile.</summary>
	private readonly State<SettingsScope> _scope = new( SettingsScope.Global );

	/// <summary>Tracks the module whose tab is shown; empty shows the first tab.</summary>
	private readonly State<string> _tab = new( "" );

	/// <summary>Stores text typed into an editor that has not been submitted with Enter yet.</summary>
	private readonly Dictionary<(SettingsScope Scope, SettingDefinition Definition), string> _drafts = [];

	/// <summary>Tracks the setting the Search palette opened the panel at, which is highlighted; empty for none.</summary>
	private readonly State<string> _focus = new( "" );

	/// <summary>Targets the highlighted setting's row, which scrolls into view once it is drawn.</summary>
	private readonly ElementHandle _focused = new();

	/// <summary>Tracks whether the highlighted setting still has to scroll into view.</summary>
	private bool _isScrollPending;

	/// <summary>Tracks the text typed into the community module search.</summary>
	private readonly State<string> _communityQuery = new( "" );

	/// <summary>Tracks whether the community list was asked for once, so a failed read is not repeated on every build.</summary>
	private bool _isCommunityRequested;

	/// <summary>Tracks the text typed into the settings search; empty shows the selected module.</summary>
	private readonly State<string> _query = new( "" );

	/// <summary>Targets the scrolling page beside the sidebar, which returns to the top when another module is shown.</summary>
	private readonly ElementHandle _main = new();

	/// <summary>Defines the sidebar's width in logical pixels.</summary>
	private const int SidebarWidth = 220;

	/// <summary>Defines the height of a module's link in the sidebar, in logical pixels.</summary>
	private const int NavItemHeight = 32;

	/// <summary>Defines the space between the sidebar's links, in logical pixels.</summary>
	private const int NavItemGap = 2;

	/// <summary>Defines the sidebar heading size in logical pixels.</summary>
	private const int SidebarHeadingSize = 18;

	/// <summary>Shows the tab holding a setting, highlights it, and scrolls it into view.</summary>
	internal void ShowSetting( SettingDefinition definition )
	{
		_tab.Value = ComputeTab( context.App.Settings.FindModule( definition ) ?? "" );
		_query.Value = "";
		_focus.Value = definition.Id;
		_isScrollPending = true;
	}

	/// <summary>Defines the modules built into the shell, whose settings share the Shell tab.</summary>
	private static readonly HashSet<string> ShellModules = ["shell", "theme", "hotload"];

	/// <summary>Defines the Shell tab's key.</summary>
	private const string ShellTab = "";

	/// <summary>Defines the Modules tab's key, which no module identifier can take.</summary>
	private const string ModulesTab = " modules";

	/// <summary>Gets the tab a module's settings show on: the Shell tab for the shell's own modules, the Modules tab for the community
	/// module's, else the module's own.</summary>
	private static string ComputeTab( string moduleId ) => moduleId == "community" ? ModulesTab : ShellModules.Contains( moduleId ) ? ShellTab : moduleId;

	/// <summary>Builds the sidebar with search and the module list, and beside it the shown module's settings, or the search results, in one card per category.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var settings = context.App.Settings;
		var scope = _scope.Value;
		var query = _query.Value.Trim();
		// The modules built into the shell share one Shell tab, first; each feature module gets its own tab, in title order.
		var owned = settings.Definitions.Where( item => !item.IsHidden )
			.GroupBy( item => ComputeTab( settings.FindModule( item ) ?? "" ) )
			.ToDictionary( group => group.Key, group => group.ToArray() );
		var tabs = context.App.Modules
			.Select( module => ShellModules.Contains( module.Id ) ? new ModuleInfo( ShellTab, "Shell", [] ) : module )
			.DistinctBy( module => module.Id )
			.Where( module => owned.ContainsKey( module.Id ) )
			// The Modules tab, which turns modules on and off, follows the Shell tab.
			.Append( new ModuleInfo( ModulesTab, "Modules", [] ) )
			.OrderBy( module => module.Id == ShellTab ? 0 : module.Id == ModulesTab ? 1 : 2 )
			.ThenBy( module => module.Title, StringComparer.CurrentCulture )
			.ToArray();
		var shown = tabs.FirstOrDefault( module => module.Id == _tab.Value ) ?? tabs.FirstOrDefault();
		var categories = (shown is null || !owned.TryGetValue( shown.Id, out var definitions ) ? [] : definitions)
			.GroupBy( item => item.Category )
			.OrderBy( group => group.Key, StringComparer.CurrentCulture )
			.ToArray();

		var header = new Container
		{
			Width = Length.Percent( 100 ),
			MaxWidth = ContentPadding + ColumnWidth + EdgeGutter,
			Padding = new Edges( 0, EdgeGutter, 0, ContentPadding ),
			Gap = theme.Spacing,
			Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					FlexWrap = FlexWrap.Wrap,
					Gap = theme.Spacing,
					Children =
					[
						new Text
						{
							Content = query.Length > 0 ? $"Results for “{query}”" : shown?.Title ?? "Settings",
							FontSize = HeadingSize,
							FontWeight = TitleWeight,
							FlexGrow = 1,
							MinWidth = 0,
							TextMaxLines = 1,
							TextTrimming = TextTrimming.Ellipsis,
						},
						BuildSegments( "Scope", [.. Enum.GetValues<SettingsScope>().Select( item => new SettingChoice( item, item == SettingsScope.Global ? "Defaults" : item.ToString() ) )],
							scope, value => _scope.Value = (SettingsScope)value ),
					],
				},
				new Text
				{
					Content = scope == SettingsScope.Global
						? $"Editing the defaults every profile inherits. Active profile: {settings.ActiveProfile}."
						: $"Editing {scope}: a changed setting overrides the default for this profile only.",
					Color = theme.Muted,
				},
			],
		};
		var children = new List<Blob>();

		if ( query.Length > 0 )
		{
			var found = tabs.Where( module => owned.ContainsKey( module.Id ) )
				.SelectMany( module => owned[module.Id]
					.Where( item => Matches( item, query ) )
					.GroupBy( item => item.Category )
					.OrderBy( group => group.Key, StringComparer.CurrentCulture )
					.Select( group => (Title: module.Id == ShellTab ? group.Key : $"{module.Title} › {group.Key}", Items: group.ToArray()) ) )
				.ToArray();
			if ( found.Length == 0 )
				children.Add( new Text { Content = "No setting matches the search.", Color = theme.Muted, Margin = new Edges( theme.Spacing * 2, 0, 0, 0 ) } );
			foreach ( var (title, items) in found )
				children.AddRange( BuildCategory( title, items, scope ) );
		}
		else
		{
			if ( shown?.Id == ModulesTab )
			{
				children.AddRange( BuildModules() );
				children.AddRange( BuildCommunity() );
			}

			foreach ( var category in categories )
				children.AddRange( BuildCategory( category.Key, [.. category], scope ) );
		}

		// The highlighted row mounts its handle with this build, so it scrolls into view after it.
		if ( _isScrollPending )
			context.Post( () => _isScrollPending = !_focused.ScrollIntoView() );

		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Settings editor",
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Children =
			[
				BuildSidebar( tabs, query.Length > 0 ? null : shown?.Id ),
				new Container
				{
					Key = "page",
					FlexGrow = 1,
					MinWidth = 0,
					MinHeight = 0,
					Padding = new Edges( ContentPadding, 0, 0, 0 ),
					Gap = theme.Spacing,
					AlignItems = AlignItems.Center,
					Children =
					[
						header,
						new Container
						{
							Key = "settings",
							Handle = _main,
							Width = Length.Percent( 100 ),
							FlexGrow = 1,
							MinHeight = 0,
							Padding = new Edges( 0, EdgeGutter, ContentPadding, ContentPadding ),
							OverflowY = Overflow.Scroll,
							ScrollbarY = ViewStyle.GetScrollbar(),
							AlignItems = AlignItems.Center,
							Children =
							[
								new Container
								{
									Width = Length.Percent( 100 ),
									MaxWidth = ColumnWidth,
									Gap = theme.Spacing,
									Children = children,
								},
							],
						},
					],
				},
			],
		};
	}

	/// <summary>Reports whether a setting's title, description or category contains the search text.</summary>
	private static bool Matches( SettingDefinition definition, string query ) =>
		definition.Title.Contains( query, StringComparison.CurrentCultureIgnoreCase )
		|| definition.Description.Contains( query, StringComparison.CurrentCultureIgnoreCase )
		|| definition.Category.Contains( query, StringComparison.CurrentCultureIgnoreCase );

	/// <summary>Builds a category's heading and its card of rows.</summary>
	private IEnumerable<Blob> BuildCategory( string title, IReadOnlyList<SettingDefinition> items, SettingsScope scope )
	{
		yield return BuildHeading( title );
		var rows = new List<Blob>();
		foreach ( var definition in items )
			rows.Add( BuildSetting( definition, scope, isFirst: rows.Count == 0 ) );
		yield return BuildCard( "category:" + title, title, rows );
	}

	/// <summary>Builds a section's heading, with controls such as a search field at its right.</summary>
	private static Row BuildHeading( string title, params Blob[] tools )
	{
		var theme = Theme.Current;
		return new Row
		{
			Key = "heading:" + title,
			AlignItems = AlignItems.Center,
			FlexWrap = FlexWrap.Wrap,
			Gap = theme.Spacing,
			Margin = new Edges( theme.Spacing * 2.5, 0, 0, theme.Spacing ),
			Children =
			[
				new Text { Content = title.ToUpper( CultureInfo.CurrentCulture ), Color = theme.Muted, FontSize = theme.FontSize - 1, FlexGrow = 1 },
				.. tools,
			],
		};
	}

	/// <summary>Builds a section's card, which holds its rows.</summary>
	private static Container BuildCard( string key, string name, List<Blob> rows )
	{
		var theme = Theme.Current;
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = name,
			BackgroundColor = theme.Raised,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius * 2,
			Overflow = Overflow.Hidden,
			Children = rows,
		};
	}

	/// <summary>Builds one row of a card: its lines, and beside them its control, which moves under the lines when the row is narrow,
	/// or always under them when <paramref name="isBelow"/> is set.</summary>
	/// <remarks>The row never shrinks: Goo holds a shrinking row at a smallest height it measures with the text at its basis width, so a long
	/// line wrapped there made the row taller than its card, and the card cut off the rows under it.</remarks>
	/// <param name="key">The row's key.</param>
	/// <param name="name">The row's accessible name.</param>
	/// <param name="lines">The title first, then the lines that describe it.</param>
	/// <param name="control">The row's widget or buttons, or null for a row of text.</param>
	/// <param name="isFirst">Whether the row is the card's first, which has no line above it.</param>
	/// <param name="isBelow">Whether the control always takes its own line under the text.</param>
	private static Container BuildRow( string key, string name, IList<Blob> lines, Blob? control, bool isFirst, bool isBelow = false )
	{
		var theme = Theme.Current;
		var text = new Container
		{
			// The text keeps at least this width; with less room the control wraps under it instead of squeezing it.
			FlexBasis = TextMinWidth,
			FlexGrow = 1000,
			FlexShrink = 1,
			MinWidth = 0,
			Gap = theme.Spacing / 2,
			Children = lines,
		};
		return new Container
		{
			Key = key,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = name,
			FlexShrink = 0,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
			Gap = theme.Spacing,
			BorderColor = theme.Border,
			BorderWidth = new Edges( isFirst ? 0 : theme.BorderWidth, 0, 0, 0 ),
			Children = control is null ? [text]
				: isBelow ? [text, control]
				:
				[
					new Row
					{
						AlignItems = AlignItems.Center,
						FlexWrap = FlexWrap.Wrap,
						Gap = theme.Spacing * 2,
						// Growing a little lets the control take the whole line once it wraps, where it sits at the right edge.
						Children = [text, new Row { FlexGrow = 1, FlexShrink = 0, JustifyContent = JustifyContent.FlexEnd, Children = [control] }],
					},
				],
		};
	}

	/// <summary>Builds one setting's row: the title, description and where the value comes from, beside its widget, or above a module's own editor.</summary>
	private Container BuildSetting( SettingDefinition definition, SettingsScope scope, bool isFirst )
	{
		var theme = Theme.Current;
		var settings = context.App.Settings;
		var isFocused = definition.Id == _focus.Value;
		// A module's own editor, such as the theme picker, takes its own line; segments sit beside the text, as other widgets do.
		var row = BuildRow( $"{scope}:{definition.Id}", definition.Title,
			[
				new Text { Content = definition.Title, FontWeight = TitleWeight },
				.. definition.Description.Length > 0 ? [new Text { Content = definition.Description, Color = theme.Muted }] : Array.Empty<Blob>(),
				.. BuildSource( definition, scope ) is { } source ? [source] : Array.Empty<Blob>(),
			],
			BuildEditor( definition, scope, settings.GetValue( definition, scope ) ), isFirst, isBelow: definition.Editor is not null );
		row.Handle = isFocused ? _focused : null;
		row.BackgroundColor = isFocused ? theme.Hover : Color.Transparent;
		// A value changed in the scope shown is marked with an accent bar at the row's left edge.
		if ( settings.HasValue( definition, scope ) )
			row.Children.Insert( 0, new Container { Position = PositionType.Absolute, Left = 0, Top = 0, Bottom = 0, Width = 2, BackgroundColor = theme.Accent } );
		return row;
	}

	/// <summary>Builds the quiet line that tells a changed value from an inherited one, with the action that resets or overrides it; null for an untouched default.</summary>
	private Row? BuildSource( SettingDefinition definition, SettingsScope scope )
	{
		var settings = context.App.Settings;
		var hasValue = settings.HasValue( definition, scope );
		if ( !hasValue && scope == SettingsScope.Global )
			return null;

		var (label, action, run) = hasValue
			? (scope == SettingsScope.Global ? "Changed from the built-in default" : $"{scope} override", scope == SettingsScope.Global ? "Reset" : "Use default",
				(Action)(() => settings.Reset( definition, scope )))
			: ("Inherits the default", "Override", () => settings.SetValue( definition, scope, settings.GetValue( definition, scope ) ));
		var button = BuildQuiet( action, () => context.Run( run ) );
		button.AccessibilityName = $"{action} {definition.Title}";
		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = Theme.Current.Spacing,
			Children = [new Text { Content = label, Color = hasValue ? Theme.Current.Accent : Theme.Current.Muted, FontSize = Theme.Current.FontSize - 1 }, button],
		};
	}

	/// <summary>Builds the widget for a setting: its module's own view, segments for choices, a switch, a stepper, or a text field.</summary>
	private Blob BuildEditor( SettingDefinition definition, SettingsScope scope, object value )
	{
		if ( definition.Editor is { } editor )
			return editor( context.App, scope );
		if ( definition.Choices is { Count: > 0 } choices )
			return BuildSegments( definition.Title, choices, value, picked => context.App.Settings.SetValue( definition, scope, picked ) );

		return definition.Kind switch
		{
			SettingKind.Boolean => BuildSwitch( definition, scope, (bool)value ),
			SettingKind.Integer => BuildStepper( definition, scope, (int)value ),
			_ => BuildField( definition, scope, value, FieldWidth ),
		};
	}

	/// <summary>Builds the sidebar: the heading, the search field, and the module list, whose selection slides to the shown module.</summary>
	private Container BuildSidebar( IReadOnlyList<ModuleInfo> tabs, string? shown )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text { Content = "Settings", FontSize = SidebarHeadingSize, FontWeight = TitleWeight, Margin = new Edges( 0, theme.Spacing * 0.75, theme.Spacing, theme.Spacing * 0.75 ) },
			new TextEntry
			{
				Key = "search",
				Value = _query.Value,
				Controlled = true,
				Placeholder = "Search settings…",
				AccessibilityName = "Search settings",
				Height = EditorHeight,
				Margin = new Edges( 0, 0, theme.Spacing, 0 ),
				Padding = new Edges( 0, theme.Spacing ),
				BackgroundColor = theme.Canvas,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius,
				FocusStyle = new Style { BorderColor = theme.Accent },
				OnChange = text => context.Run( () => _query.Value = text ),
			},
		};

		var items = new List<Blob>();
		var index = -1;
		foreach ( var module in tabs )
		{
			var isShown = module.Id == shown;
			if ( isShown )
				index = items.Count;
			items.Add( BuildNavItem( module, isShown ) );
		}

		// One selection rectangle behind the items moves between them, rather than each item lighting up on its own.
		if ( index >= 0 )
		{
			var duration = context.App.Settings.Get( ShellSettings.MoveDuration );
			items.Insert( 0, new Container
			{
				Key = "selection",
				Position = PositionType.Absolute,
				Left = 0,
				Right = 0,
				Top = index * (NavItemHeight + NavItemGap),
				Height = NavItemHeight,
				BorderRadius = theme.Radius,
				BackgroundColor = theme.Hover,
				LayoutTransition = duration > 0 ? new( duration, Easing.EaseOut ) : null,
			} );
		}

		children.Add( new Container
		{
			Key = "modules",
			FlexGrow = 1,
			MinHeight = 0,
			Padding = new Edges( 0, ViewStyle.TrackWidth, 0, 0 ),
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.GetScrollbar(),
			Children = [new Container { Gap = NavItemGap, Children = items }],
		} );

		return new Container
		{
			Key = "sidebar",
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Settings sections",
			Width = SidebarWidth,
			FlexShrink = 0,
			Padding = new Edges( ContentPadding * 0.75, theme.Spacing * 1.25 ),
			MinHeight = 0,
			Gap = 0,
			BorderColor = theme.Divider,
			BorderWidth = new Edges( 0, theme.BorderWidth, 0, 0 ),
			Children = children,
		};
	}

	/// <summary>Builds one module's link in the sidebar, bright and bold when shown; the selection rectangle behind it marks it.</summary>
	private Button BuildNavItem( ModuleInfo module, bool isShown )
	{
		var theme = Theme.Current;
		void Show() => context.Run( () =>
		{
			_tab.Value = module.Id;
			_ = _main.ScrollTo( null, 0 );
			_query.Value = "";
			_focus.Value = "";
		} );
		return new Button
		{
			Key = "tab:" + module.Id,
			Height = NavItemHeight,
			Padding = new Edges( 0, theme.Spacing * 1.25 ),
			JustifyContent = JustifyContent.Center,
			AlignItems = AlignItems.FlexStart,
			BorderRadius = theme.Radius,
			BackgroundColor = Color.Transparent,
			Color = isShown ? theme.Text : theme.Muted,
			Focusable = true,
			Cursor = Cursor.Pointer,
			AccessibilityName = isShown ? $"{module.Title} settings, shown" : $"{module.Title} settings",
			OnClick = Show,
			KeyBindings =
			[
				new( Key.Enter, KeyModifiers.None, Show ),
				new( Key.Space, KeyModifiers.None, Show ),
			],
			HoverStyle = isShown ? new Style { Color = theme.Text } : new Style { Color = theme.Text, BackgroundColor = theme.Raised },
			FocusStyle = new Style { Color = theme.Text, BorderColor = theme.Accent },
			BorderWidth = theme.BorderWidth,
			BorderColor = Color.Transparent,
			Children = [new Text { Content = module.Title, FontWeight = isShown ? TitleWeight : 400, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis }],
		};
	}

	/// <summary>Builds a segmented control with the picked value raised.</summary>
	private Row BuildSegments( string name, IReadOnlyList<SettingChoice> choices, object picked, Action<object> pick ) =>
		Theme.Current.BuildSegments( name, [.. choices.Select( choice => (choice.Value, choice.Label) )], picked, value => context.Run( () => pick( value ) ) );

	/// <summary>Builds an on and off switch for a boolean setting.</summary>
	private Button BuildSwitch( SettingDefinition definition, SettingsScope scope, bool isOn ) =>
		BuildToggle( definition.Title, isOn, isEnabled: true, () => context.App.Settings.SetValue( definition, scope, !isOn ) );

	/// <summary>Builds an on and off switch whose flip runs guarded by the panel.</summary>
	private Button BuildToggle( string name, bool isOn, bool isEnabled, Action toggle ) =>
		Theme.Current.BuildSwitch( name, isOn, () => context.Run( toggle ), isEnabled );

	/// <summary>Builds the Modules tab: every installed module in a card, with what it needs, what needs it, and a switch that turns it on or off.</summary>
	/// <remarks>
	/// Turning a module on also turns on the modules it needs; a module that others need stays on until they are off; the shell's own modules
	/// always stay on. The choice is kept across restarts.
	/// </remarks>
	private IEnumerable<Blob> BuildModules()
	{
		var theme = Theme.Current;
		var app = context.App;
		var loaded = app.Modules.ToDictionary( module => module.Id );
		var known = app.AvailableModules.ToDictionary( module => module.Id );
		string Title( string id ) => known.GetValueOrDefault( id )?.Title ?? id;
		var groups = app.AvailableModules
			.GroupBy( module => !module.CanUnload || ShellModules.Contains( module.Id ) ? "Built into the shell" : "Features" )
			.OrderBy( group => group.Key == "Built into the shell" ? 0 : 1 );
		foreach ( var group in groups )
		{
			yield return BuildHeading( group.Key );
			var rows = new List<Blob>();
			foreach ( var module in group.OrderBy( module => module.Title, StringComparer.CurrentCulture ) )
			{
				var isOn = loaded.ContainsKey( module.Id );
				var needs = module.Dependencies;
				var neededBy = loaded.Values.Where( other => other.Dependencies.Contains( module.Id ) ).Select( other => other.Title ).ToArray();
				var missing = ComputeOffNeeds( module, known, loaded );
				var isInstalled = missing.All( known.ContainsKey );
				var note = !module.CanUnload ? "Always on."
					: isOn && neededBy.Length > 0 ? $"Needed by {string.Join( ", ", neededBy )}."
					: !isOn && !isInstalled ? $"Needs {string.Join( ", ", missing.Where( id => !known.ContainsKey( id ) ) )}, which is not installed."
					: !isOn && missing.Count > 0 ? $"Also turns on {string.Join( ", ", missing.Select( Title ) )}."
					: null;
				var isEnabled = module.CanUnload && (isOn ? neededBy.Length == 0 : isInstalled);
				List<Blob> lines =
				[
					new Row
					{
						Gap = theme.Spacing,
						AlignItems = AlignItems.Center,
						Children =
						[
							new Text { Content = module.Title, FontWeight = TitleWeight },
							new Text { Content = module.Id, Color = theme.Muted, FontSize = theme.FontSize - 1 },
						],
					},
					.. needs.Count > 0 ? [new Text { Content = $"Uses {string.Join( ", ", needs.Select( Title ) )}.", Color = theme.Muted }] : Array.Empty<Blob>(),
					.. note is not null ? [new Text { Content = note, Color = isEnabled ? theme.Muted : theme.Accent, FontSize = theme.FontSize - 1 }] : Array.Empty<Blob>(),
				];
				var toggle = BuildToggle( module.Title, isOn, isEnabled, () =>
				{
					if ( isOn )
						app.Unload( module.Id );
					else
					{
						// Needed modules first, in the order they need each other.
						foreach ( var id in missing )
							app.Enable( id );
						app.Enable( module.Id );
					}
				} );
				rows.Add( BuildRow( "module:" + module.Id, module.Title, lines, toggle, isFirst: rows.Count == 0 ) );
			}

			yield return BuildCard( "modules:" + group.Key, group.Key, rows );
		}
	}

	/// <summary>Builds the Community section of the Modules tab: a search field, then every hub module and every installed one, each with
	/// Install, Update or Remove.</summary>
	/// <remarks>The hub is read the first time the tab shows; Refresh reads it again.</remarks>
	private IEnumerable<Blob> BuildCommunity()
	{
		var theme = Theme.Current;
		var app = context.App;
		if ( app.AvailableModules.OfType<CommunityModule>().FirstOrDefault() is not { } community )
			yield break;

		if ( community.Index is null && !community.IsLoading && !_isCommunityRequested )
		{
			_isCommunityRequested = true;
			context.Post( () => community.Refresh() );
		}

		yield return BuildHeading( "Community",
			new TextEntry
			{
				Value = _communityQuery.Value,
				Controlled = true,
				Placeholder = "Search community modules",
				Width = FieldWidth,
				Height = EditorHeight,
				Padding = new Edges( 0, theme.Spacing ),
				BackgroundColor = theme.Canvas,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius + 2,
				AccessibilityName = "Search community modules",
				OnChange = text => _communityQuery.Value = text,
			},
			BuildQuiet( community.IsLoading ? "Loading…" : "Refresh", () => community.Refresh(), isEnabled: !community.IsLoading, name: "Refresh community modules" ) );
		yield return new Text
		{
			Content = $"From {community.SourceLabel}. Each module is reviewed before it joins the hub, but it runs with your rights: install only what you trust.",
			Color = theme.Muted,
			FontSize = theme.FontSize - 1,
			Margin = new Edges( 0, 0, theme.Spacing, 0 ),
		};

		var installed = community.Installed.ToDictionary( module => module.Id );
		var offered = community.Index?.Modules ?? [];
		var query = _communityQuery.Value.Trim();
		bool Matches( string text ) => text.Contains( query, StringComparison.CurrentCultureIgnoreCase );
		var entries = offered
			.Select( module => (module.Id, Hub: (HubModule?)module, Name: module.Name) )
			.Concat( installed.Values.Where( module => offered.All( item => item.Id != module.Id ) ).Select( module => (module.Id, Hub: (HubModule?)null, module.Name) ) )
			.Where( entry => query.Length == 0 || Matches( entry.Name ) || Matches( entry.Id ) || entry.Hub is { } hub && (Matches( hub.Author ) || Matches( hub.Description )) )
			.OrderBy( entry => installed.ContainsKey( entry.Id ) ? 0 : 1 )
			.ThenBy( entry => entry.Name, StringComparer.CurrentCulture )
			.ToArray();

		var rows = new List<Blob>();
		Blob BuildMessage( string text, Color color ) => BuildRow( "message", text, [new Text { Content = text, Color = color }], null, isFirst: rows.Count == 0 );
		if ( community.Error is { } error )
			rows.Add( BuildMessage( $"Could not read the module list: {error}", theme.Accent ) );
		else if ( community.Index is null )
			rows.Add( BuildMessage( "Loading the module list…", theme.Muted ) );

		foreach ( var (id, hub, name) in entries )
			rows.Add( BuildCommunityRow( community, id, hub, name, installed.GetValueOrDefault( id ), isFirst: rows.Count == 0 ) );

		if ( rows.Count == 0 )
			rows.Add( BuildMessage( query.Length > 0 ? "No community module matches." : "The hub has no modules yet.", theme.Muted ) );

		yield return BuildCard( "community", "Community modules", rows );
	}

	/// <summary>Builds one community module's row: its name, version and author, what it does, its state, and its buttons.</summary>
	private Container BuildCommunityRow( CommunityModule community, string id, HubModule? hub, string name, InstalledModule? installed, bool isFirst )
	{
		var theme = Theme.Current;
		var app = context.App;
		var isBusy = community.IsBusy( id );
		var isUpdate = hub is not null && installed is not null && CommunityHub.ParseVersion( hub.Version ) > CommunityHub.ParseVersion( installed.Version );
		var isLoaded = app.Modules.Any( module => module.Id == id );
		var isKnown = app.AvailableModules.Any( module => module.Id == id );
		var (note, isWarning) = (isBusy, installed, hub) switch
		{
			(true, _, _) => ("Working…", false),
			(_, null, _) => ((string?)null, false),
			(_, _, null) => ("Installed. This module is no longer in the hub.", true),
			_ when isUpdate => ($"Version {installed.Version} installed.", false),
			_ when isLoaded => ("Installed.", false),
			_ when isKnown => ("Installed, and turned off above.", false),
			_ => ("Installed, but it did not load with this version of Loom. The error notice says why.", true),
		};

		var buttons = new List<Blob>();
		if ( hub is not null && installed is null )
			buttons.Add( theme.BuildButton( "Install", () => community.Install( hub ), !isBusy ) );
		if ( hub is not null && isUpdate )
			buttons.Add( theme.BuildButton( $"Update to {hub.Version}", () => community.Install( hub ), !isBusy ) );
		if ( installed is not null )
			buttons.Add( BuildQuiet( "Remove", () => community.Remove( id ), !isBusy, $"Remove {name}" ) );

		var heading = new List<Blob> { new Text { Content = name, FontWeight = TitleWeight } };
		if ( (hub?.Version ?? installed?.Version) is { Length: > 0 } version )
			heading.Add( new Text { Content = version, Color = theme.Muted, FontSize = theme.FontSize - 1 } );
		if ( hub is not null )
			heading.Add( new Text { Content = $"by {hub.Author}", Color = theme.Muted, FontSize = theme.FontSize - 1 } );

		List<Blob> lines =
		[
			new Row { Gap = theme.Spacing, AlignItems = AlignItems.Center, Children = heading },
			.. hub is not null ? [new Text { Content = hub.Description, Color = theme.Muted }] : Array.Empty<Blob>(),
			.. note is not null ? [new Text { Content = note, Color = isWarning ? theme.Accent : theme.Muted, FontSize = theme.FontSize - 1 }] : Array.Empty<Blob>(),
		];
		return BuildRow( "community:" + id, name, lines, buttons.Count > 0 ? new Row { Gap = theme.Spacing, Children = buttons } : null, isFirst );
	}

	/// <summary>Lists the modules that are off and that a module needs, directly or through another, those needed first.</summary>
	private static List<string> ComputeOffNeeds( ILoknModule module, IReadOnlyDictionary<string, ILoknModule> known, IReadOnlyDictionary<string, ModuleInfo> loaded )
	{
		var order = new List<string>();
		void Visit( string id )
		{
			if ( loaded.ContainsKey( id ) || order.Contains( id ) )
				return;
			if ( known.TryGetValue( id, out var need ) )
			{
				foreach ( var next in need.Dependencies )
					Visit( next );
			}
			order.Add( id );
		}

		foreach ( var id in module.Dependencies )
			Visit( id );
		return order;
	}

	/// <summary>Builds a stepper: minus, the value with its unit, which takes typing too, and plus; each button moves by the setting's step within its bounds.</summary>
	private Row BuildStepper( SettingDefinition definition, SettingsScope scope, int value )
	{
		var theme = Theme.Current;
		Button BuildStep( string glyph, string name, int target ) => BuildQuiet( glyph, () => context.Run( () => context.App.Settings.SetValue( definition, scope, target ) ),
			isEnabled: target != value && definition.IsValidValue( target ), name: $"{name} {definition.Title}" );
		var down = BuildStep( "−", "Decrease", Math.Max( definition.Minimum, value - definition.Step ) );
		var up = BuildStep( "+", "Increase", Math.Min( definition.Maximum, value + definition.Step ) );
		var field = BuildField( definition, scope, value, StepperValueWidth );
		field.BorderWidth = 0;
		field.BackgroundColor = Color.Transparent;
		var children = new List<Blob> { down, field };
		if ( definition.Unit is { } unit )
			children.Add( new Text { Content = unit, Color = theme.Muted, Margin = new Edges( 0, theme.Spacing, 0, 0 ) } );
		children.Add( up );
		return new Row
		{
			Height = EditorHeight,
			AlignItems = AlignItems.Center,
			Padding = new Edges( 0, 2 ),
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + 2,
			Children = children,
		};
	}

	/// <summary>Builds a text field that saves on Enter or when it loses focus.</summary>
	private TextEntry BuildField( SettingDefinition definition, SettingsScope scope, object value, int width ) => new()
	{
		Value = Convert.ToString( value, CultureInfo.InvariantCulture ) ?? "",
		Width = width,
		Height = EditorHeight,
		Padding = new Edges( 0, Theme.Current.Spacing ),
		BackgroundColor = Theme.Current.Canvas,
		BorderColor = Theme.Current.Border,
		BorderWidth = Theme.Current.BorderWidth,
		BorderRadius = Theme.Current.Radius + 2,
		AccessibilityName = definition.Title,
		OnChange = text => _drafts[(scope, definition)] = text,
		OnBlur = _ => context.Run( () => UpdateDraft( definition, scope ) ),
		OnSubmit = text => context.Run( () =>
		{
			_drafts.Remove( (scope, definition) );
			UpdateValue( definition, scope, text );
		} ),
	};

	/// <summary>Builds a small borderless button that lights up under the pointer.</summary>
	private static Button BuildQuiet( string label, Action action, bool isEnabled = true, string? name = null )
	{
		var theme = Theme.Current;
		var button = theme.BuildButton( label, action, isEnabled );
		button.BackgroundColor = Color.Transparent;
		button.BorderWidth = 0;
		button.Height = EditorHeight - 4;
		button.Padding = new Edges( 0, theme.Spacing );
		button.HoverStyle = new Style { BackgroundColor = theme.Hover };
		if ( name is not null )
			button.AccessibilityName = name;
		return button;
	}

	/// <summary>Saves edits still pending when the panel closes, so they are not silently discarded.</summary>
	public override void Closed()
	{
		foreach ( var (scope, definition) in _drafts.Keys.ToArray() )
		{
			// At application exit a feature module may already be unloaded; its setting can no longer be stored.
			if ( context.App.Settings.Definitions.Contains( definition ) )
				UpdateDraft( definition, scope );
		}
	}

	/// <summary>Saves text left in an editor without Enter, unless it still matches the displayed value.</summary>
	private void UpdateDraft( SettingDefinition definition, SettingsScope scope )
	{
		if ( !_drafts.Remove( (scope, definition), out var text ) )
			return;

		// Storing an unchanged value would turn an inherited setting into an explicit override.
		var shown = Convert.ToString( context.App.Settings.GetValue( definition, scope ), CultureInfo.InvariantCulture );
		if ( text == shown )
			return;

		UpdateValue( definition, scope, text );
	}

	/// <summary>Parses a submitted editor value before applying the definition's validation rules.</summary>
	private void UpdateValue( SettingDefinition definition, SettingsScope scope, string text )
	{
		if ( definition.Kind == SettingKind.Integer )
		{
			if ( int.TryParse( text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value ) )
				context.App.Settings.SetValue( definition, scope, value );
			else
				context.App.ReportError( definition.Title, new ArgumentException( "Enter a whole number." ) );

			return;
		}

		context.App.Settings.SetValue( definition, scope, text );
	}
}
