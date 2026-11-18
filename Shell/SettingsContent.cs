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

	/// <summary>Defines the widest the settings column grows, in logical pixels.</summary>
	private const int ColumnWidth = 720;

	/// <summary>Defines the settings heading size in logical pixels.</summary>
	private const int HeadingSize = 22;

	/// <summary>Defines the height of fields and steppers in logical pixels.</summary>
	private const int EditorHeight = 32;

	/// <summary>Defines the text field's width in logical pixels.</summary>
	private const int FieldWidth = 240;

	/// <summary>Defines the switch's width in logical pixels.</summary>
	private const int SwitchWidth = 40;

	/// <summary>Defines the switch's height in logical pixels.</summary>
	private const int SwitchHeight = 22;

	/// <summary>Defines the gap between the switch's track and knob in logical pixels.</summary>
	private const int KnobInset = 3;

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

	/// <summary>Shows the tab holding a setting, highlights it, and scrolls it into view.</summary>
	internal void ShowSetting( SettingDefinition definition )
	{
		_tab.Value = ComputeTab( context.App.Settings.FindModule( definition ) ?? "" );
		_focus.Value = definition.Id;
		_isScrollPending = true;
	}

	/// <summary>Defines the modules built into the shell, whose settings share the Shell tab.</summary>
	private static readonly HashSet<string> ShellModules = ["shell", "theme", "hotload"];

	/// <summary>Defines the Shell tab's key.</summary>
	private const string ShellTab = "";

	/// <summary>Defines the Modules tab's key, which no module identifier can take.</summary>
	private const string ModulesTab = " modules";

	/// <summary>Gets the tab a module's settings show on: the Shell tab for the shell's own modules, else the module's own.</summary>
	private static string ComputeTab( string moduleId ) => ShellModules.Contains( moduleId ) ? ShellTab : moduleId;

	/// <summary>Builds the scope selector, the module tabs, and the shown module's settings in one card per category.</summary>
	public override Blob Build()
	{
		var theme = Theme.Current;
		var settings = context.App.Settings;
		var scope = _scope.Value;
		// The modules built into the shell share one Shell tab, first; each feature module gets its own tab, in title order.
		var owned = settings.Definitions
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
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Settings", FontSize = HeadingSize, FlexGrow = 1 },
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
			BuildTabs( tabs, shown?.Id ),
		};

		if ( shown?.Id == ModulesTab )
			children.AddRange( BuildModules() );

		var categories = (shown is null || !owned.TryGetValue( shown.Id, out var definitions ) ? [] : definitions)
			.GroupBy( item => item.Category )
			.OrderBy( group => group.Key, StringComparer.CurrentCulture );
		foreach ( var category in categories )
		{
			children.Add( new Text
			{
				Content = category.Key,
				Color = theme.Muted,
				Margin = new Edges( theme.Spacing * 2, 0, 0, theme.Spacing ),
			} );
			var rows = new List<Blob>();
			foreach ( var definition in category )
				rows.Add( BuildSetting( definition, scope, isFirst: rows.Count == 0 ) );
			children.Add( new Container
			{
				Key = "category:" + category.Key,
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = category.Key,
				BackgroundColor = theme.Raised,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius * 2,
				Overflow = Overflow.Hidden,
				Children = rows,
			} );
		}

		// The highlighted row mounts its handle with this build, so it scrolls into view after it.
		if ( _isScrollPending )
			context.Post( () => _isScrollPending = !_focused.ScrollIntoView() );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = "Settings editor",
			Padding = ContentPadding,
			OverflowY = Overflow.Scroll,
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
		};
	}

	/// <summary>Builds one row: the title, description and where the value comes from, beside its widget, or above its segments.</summary>
	private Container BuildSetting( SettingDefinition definition, SettingsScope scope, bool isFirst )
	{
		var theme = Theme.Current;
		var settings = context.App.Settings;
		var value = settings.GetValue( definition, scope );
		var text = new Container
		{
			// The text keeps at least this width; with less room the widget wraps under it instead of squeezing it.
			FlexBasis = TextMinWidth,
			FlexGrow = 1000,
			FlexShrink = 1,
			MinWidth = 0,
			Gap = theme.Spacing / 2,
			Children =
			[
				new Text { Content = definition.Title, FontWeight = TitleWeight },
				.. definition.Description.Length > 0 ? [new Text { Content = definition.Description, Color = theme.Muted }] : Array.Empty<Blob>(),
				.. BuildSource( definition, scope ) is { } source ? [source] : Array.Empty<Blob>(),
			],
		};
		var editor = BuildEditor( definition, scope, value );
		var isBelow = definition.Editor is not null || definition.Choices is { Count: > 0 };
		var isFocused = definition.Id == _focus.Value;
		return new Container
		{
			Key = $"{scope}:{definition.Id}",
			Handle = isFocused ? _focused : null,
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = definition.Title,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
			Gap = theme.Spacing,
			BackgroundColor = isFocused ? theme.Hover : Color.Transparent,
			BorderColor = theme.Border,
			BorderWidth = new Edges( isFirst ? 0 : theme.BorderWidth, 0, 0, 0 ),
			// A module's own editor, such as the theme picker, takes the full width; segments sit at the right edge.
			Children = isBelow
				? [text, definition.Editor is not null ? editor : new Row { JustifyContent = JustifyContent.FlexEnd, Children = [editor] }]
				:
				[
					new Row
					{
						AlignItems = AlignItems.Center,
						FlexWrap = FlexWrap.Wrap,
						Gap = theme.Spacing * 2,
						// Growing a little lets the widget take the whole line once it wraps, where it sits at the right edge.
						Children = [text, new Row { FlexGrow = 1, FlexShrink = 0, JustifyContent = JustifyContent.FlexEnd, Children = [editor] }],
					},
				],
		};
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

	/// <summary>Builds the module tabs: a full-width bar of module titles, the shown one bright and underlined in the accent color.</summary>
	private Row BuildTabs( IReadOnlyList<ModuleInfo> tabs, string? shown )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		foreach ( var module in tabs )
		{
			var isShown = module.Id == shown;
			void Show() => context.Run( () =>
			{
				_tab.Value = module.Id;
				_focus.Value = "";
			} );
			children.Add( new Button
			{
				Key = "tab:" + module.Id,
				Padding = new Edges( theme.Spacing, theme.Spacing * 1.5 ),
				Margin = new Edges( 0, 0, -theme.BorderWidth, 0 ),
				BackgroundColor = Color.Transparent,
				BorderColor = isShown ? theme.Accent : Color.Transparent,
				BorderWidth = new Edges( 0, 0, 2, 0 ),
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
				HoverStyle = new Style { Color = theme.Text },
				FocusStyle = new Style { Color = theme.Text },
				Children = [new Text { Content = module.Title, FontWeight = isShown ? TitleWeight : 400 }],
			} );
		}

		return new Row
		{
			Key = "tabs",
			AccessibilityRole = AccessibilityRole.Toolbar,
			AccessibilityName = "Module settings",
			FlexWrap = FlexWrap.Wrap,
			Gap = theme.Spacing,
			Margin = new Edges( theme.Spacing, 0, 0, 0 ),
			BorderColor = theme.Border,
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			Children = children,
		};
	}

	/// <summary>Builds a segmented control with the picked value raised.</summary>
	private Row BuildSegments( string name, IReadOnlyList<SettingChoice> choices, object picked, Action<object> pick )
	{
		var theme = Theme.Current;
		var segments = new List<Blob>();
		foreach ( var choice in choices )
		{
			var isPicked = Equals( choice.Value, picked );
			void Pick() => context.Run( () => pick( choice.Value ) );
			segments.Add( new Button
			{
				Key = "segment:" + choice.Label,
				Padding = new Edges( theme.Spacing * 0.75, theme.Spacing * 1.75 ),
				BorderRadius = theme.Radius,
				BackgroundColor = isPicked ? theme.Panel : Color.Transparent,
				BorderColor = isPicked ? theme.Border : Color.Transparent,
				BorderWidth = theme.BorderWidth,
				Color = isPicked ? theme.Text : theme.Muted,
				Focusable = true,
				Cursor = Cursor.Pointer,
				AccessibilityName = isPicked ? $"{choice.Label}, selected" : choice.Label,
				OnClick = Pick,
				KeyBindings =
				[
					new( Key.Enter, KeyModifiers.None, Pick ),
					new( Key.Space, KeyModifiers.None, Pick ),
				],
				HoverStyle = new Style { Color = theme.Text },
				FocusStyle = new Style { BorderColor = theme.Accent },
				Children = [new Text { Content = choice.Label, FontWeight = isPicked ? TitleWeight : 400 }],
			} );
		}

		return new Row
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = name,
			AlignSelf = AlignSelf.FlexStart,
			FlexWrap = FlexWrap.Wrap,
			Padding = 2,
			Gap = 2,
			BackgroundColor = theme.Canvas,
			BorderColor = theme.Border,
			BorderWidth = theme.BorderWidth,
			BorderRadius = theme.Radius + 2,
			Children = segments,
		};
	}

	/// <summary>Builds an on and off switch: a track in the accent color with the knob at the end when on.</summary>
	private Button BuildSwitch( SettingDefinition definition, SettingsScope scope, bool isOn ) =>
		BuildToggle( definition.Title, isOn, isEnabled: true, () => context.App.Settings.SetValue( definition, scope, !isOn ) );

	/// <summary>Builds an on and off switch: a track in the accent color with the knob at the end when on; a disabled one is dimmed and ignores clicks.</summary>
	private Button BuildToggle( string name, bool isOn, bool isEnabled, Action toggle )
	{
		var theme = Theme.Current;
		void Toggle() => context.Run( toggle );
		const int knob = SwitchHeight - KnobInset * 2;
		return new Button
		{
			Key = "switch",
			Width = SwitchWidth,
			Height = SwitchHeight,
			Padding = KnobInset,
			FlexDirection = FlexDirection.Row,
			JustifyContent = isOn ? JustifyContent.FlexEnd : JustifyContent.FlexStart,
			BorderRadius = SwitchHeight / 2.0,
			BackgroundColor = isOn ? theme.Accent : theme.Hover,
			Opacity = isEnabled ? 1 : 0.4,
			Focusable = isEnabled,
			Cursor = isEnabled ? Cursor.Pointer : Cursor.Default,
			AccessibilityName = $"{name}: {(isOn ? "on" : "off")}",
			OnClick = isEnabled ? Toggle : null,
			KeyBindings = isEnabled ? [new( Key.Enter, KeyModifiers.None, Toggle ), new( Key.Space, KeyModifiers.None, Toggle )] : [],
			FocusStyle = new Style { BorderColor = theme.Text, BorderWidth = theme.BorderWidth },
			Children = [new Container { Width = knob, Height = knob, BorderRadius = knob / 2.0, BackgroundColor = isOn ? theme.Canvas : theme.Muted }],
		};
	}

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
			yield return new Text { Content = group.Key, Color = theme.Muted, Margin = new Edges( theme.Spacing * 2, 0, 0, theme.Spacing ) };
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
				var text = new Container
				{
					FlexBasis = TextMinWidth,
					FlexGrow = 1000,
					FlexShrink = 1,
					MinWidth = 0,
					Gap = theme.Spacing / 2,
					Children =
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
					],
				};
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
				rows.Add( new Container
				{
					Key = "module:" + module.Id,
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = module.Title,
					Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
					BorderColor = theme.Border,
					BorderWidth = new Edges( rows.Count == 0 ? 0 : theme.BorderWidth, 0, 0, 0 ),
					Children =
					[
						new Row
						{
							AlignItems = AlignItems.Center,
							FlexWrap = FlexWrap.Wrap,
							Gap = theme.Spacing * 2,
							Children = [text, new Row { FlexGrow = 1, FlexShrink = 0, JustifyContent = JustifyContent.FlexEnd, Children = [toggle] }],
						},
					],
				} );
			}

			yield return new Container
			{
				Key = "modules:" + group.Key,
				AccessibilityRole = AccessibilityRole.Group,
				AccessibilityName = group.Key,
				BackgroundColor = theme.Raised,
				BorderColor = theme.Border,
				BorderWidth = theme.BorderWidth,
				BorderRadius = theme.Radius * 2,
				Overflow = Overflow.Hidden,
				Children = rows,
			};
		}
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
