using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Configures a model picker for one account profile.</summary>
/// <param name="Service">The service that lists the profile's models.</param>
/// <param name="Profile">The account profile whose models are listed.</param>
/// <param name="Current">The selector in use where the picker opened, or empty.</param>
/// <param name="Default">The profile's default selector, or empty for Omp's own default.</param>
/// <param name="UseLabel">The label of the main action, such as "Use for this chat".</param>
/// <param name="Use">Applies the chosen model with the main action.</param>
/// <param name="MakeDefault">Saves the chosen model as the profile's default, or null to leave that action out.</param>
/// <param name="OpenSetup">Opens Omp setup for accounts and sign-in, or null to leave that action out.</param>
/// <param name="OpenRouting">Opens Omp's model roles, fallback chains and presets, or null to leave that action out.</param>
/// <param name="Close">Closes the picker.</param>
public sealed record OmpModelPickerInput(
	OmpService Service,
	SettingsProfile Profile,
	string Current,
	string Default,
	string UseLabel,
	Action<OmpModelInfo> Use,
	Action<OmpModelInfo>? MakeDefault,
	Action? OpenSetup,
	Action? OpenRouting,
	Action Close );

/// <summary>Shows a searchable, keyboard-driven model list over its panel: recent models first, then every available one, with limits and prices.</summary>
public sealed class OmpModelPicker : GuardedCell<OmpModelPickerInput>
{
	/// <summary>Defines the widest the picker grows, in logical pixels.</summary>
	private const int PickerWidth = 760;

	/// <summary>Defines the picker's share of its panel height.</summary>
	private const double PickerHeightPercent = 85;

	/// <summary>Defines the space kept around the picker inside its panel, in logical pixels.</summary>
	private const int PickerMargin = 24;

	/// <summary>Defines the picker's corner radius in logical pixels.</summary>
	private const int PickerRadius = 12;

	/// <summary>Defines the opacity of the backdrop that dims the panel behind the picker.</summary>
	private const double BackdropOpacity = 0.6;

	/// <summary>Defines the context column width in logical pixels.</summary>
	private const int ContextWidth = 56;

	/// <summary>Defines the price column width in logical pixels.</summary>
	private const int PriceWidth = 104;

	/// <summary>Defines the check column width in logical pixels.</summary>
	private const int CheckWidth = 18;

	/// <summary>Defines the title and badge emphasis weight.</summary>
	private const int TitleWeight = 600;

	/// <summary>Defines metadata text size in logical pixels.</summary>
	private const int SmallSize = 12;

	/// <summary>Holds the check drawn beside the model in use.</summary>
	private static readonly VectorPath Check = VectorPath.Parse( "M2.5 6.5 L5 9 L9.5 3.5", new ElementRect { Width = 12, Height = 12 } );

	/// <summary>Retains the listed models; empty until the first list arrives.</summary>
	private readonly State<IReadOnlyList<OmpModelInfo>> _models = new( [] );

	/// <summary>Reports whether a list has arrived, so an empty one reads as "none available" rather than "loading".</summary>
	private readonly State<bool> _isLoaded = new( false );

	/// <summary>Retains why the models could not be listed.</summary>
	private readonly State<string> _error = new( "" );

	/// <summary>Retains the search text.</summary>
	private readonly State<string> _query = new( "" );

	/// <summary>Retains the highlighted row's position in the visible list.</summary>
	private readonly State<int> _highlight = new( 0 );

	/// <summary>Targets the search field, which takes focus when the picker opens.</summary>
	private readonly ElementHandle _search = new();

	/// <summary>Targets the highlighted row, which keyboard moves scroll into view.</summary>
	private readonly ElementHandle _highlighted = new();

	/// <summary>Shows the last catalog for the profile at once, when there is one, before the first build.</summary>
	protected override void InputChanged( OmpModelPickerInput input )
	{
		if ( !_isLoaded.Value && input.Service.GetCachedModels( input.Profile ) is { } cached )
			UpdateModels( cached );
	}

	/// <summary>Focuses the search and lists the profile's models again, so a new sign-in or account shows up.</summary>
	protected override void Mounted()
	{
		_search.Focus();
		_ = UpdateModelsAsync();
	}

	/// <summary>Builds the dimmed backdrop and the picker card: search, model list, details of the highlighted model, and actions.</summary>
	protected override Blob BuildView( OmpModelPickerInput input )
	{
		var rows = ComputeRows( input );
		var highlight = rows.Count == 0 ? -1 : Math.Clamp( _highlight.Value, 0, rows.Count - 1 );
		var chosen = highlight < 0 ? null : rows[highlight].Model;
		var theme = Theme.Current;
		return new Container
		{
			Key = "model-picker",
			Position = PositionType.Absolute,
			Left = 0,
			Top = 0,
			Right = 0,
			Bottom = 0,
			Padding = PickerMargin,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Children =
			[
				// The backdrop sits behind the card as a sibling, so a click on the card never reaches it.
				new Container
				{
					Position = PositionType.Absolute,
					Left = 0,
					Top = 0,
					Right = 0,
					Bottom = 0,
					BackgroundColor = theme.Canvas.WithAlpha( BackdropOpacity ),
					OnPointerDown = _ => UpdateClose(),
				},
				new Container
				{
					AccessibilityRole = AccessibilityRole.Group,
					AccessibilityName = "Switch model",
					Width = Length.Percent( 100 ),
					MaxWidth = PickerWidth,
					MaxHeight = Length.Percent( PickerHeightPercent ),
					MinHeight = 0,
					Overflow = Overflow.Hidden,
					BackgroundColor = theme.Raised,
					BorderColor = theme.Border,
					BorderWidth = theme.BorderWidth,
					BorderRadius = PickerRadius,
					Children =
					[
						BuildHeader( rows.Count, highlight, rows, input ),
						BuildList( rows, highlight, input ),
						BuildFooter( chosen, input ),
					],
				},
			],
		};
	}

	/// <summary>Builds the title and the search field that drives the list from the keyboard.</summary>
	private Row BuildHeader( int count, int highlight, IReadOnlyList<PickerRow> rows, OmpModelPickerInput input )
	{
		var theme = Theme.Current;
		void Move( int step )
		{
			if ( rows.Count == 0 )
				return;
			_highlight.Value = Math.Clamp( highlight + step, 0, rows.Count - 1 );
			// The highlighted row mounts its handle on the next build, so it scrolls into view after that.
			Post( () => _highlighted.ScrollIntoView() );
		}

		return new Row
		{
			Key = "model-picker-header",
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing * 1.5,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
			BorderWidth = new Edges( 0, 0, theme.BorderWidth, 0 ),
			BorderColor = theme.Border,
			Children =
			[
				new Text { Content = "Switch model", FontWeight = TitleWeight, Color = theme.Text, FlexShrink = 0 },
				new Container { Width = theme.BorderWidth, Height = theme.ControlHeight * 0.6, BackgroundColor = theme.Border, FlexShrink = 0 },
				new TextEntry
				{
					Handle = _search,
					Value = _query.Value,
					Controlled = true,
					Placeholder = "Search models",
					AccessibilityName = "Search models",
					FlexGrow = 1,
					MinWidth = 0,
					Padding = 0,
					BorderWidth = 0,
					BackgroundColor = Color.Transparent,
					OnChange = value =>
					{
						_query.Value = value;
						_highlight.Value = 0;
					},
					OnSubmit = _ =>
					{
						if ( highlight >= 0 )
							UpdateUse( rows[highlight].Model, input );
					},
					KeyBindings =
					[
						new( Key.Down, KeyModifiers.None, () => Move( 1 ) ),
						new( Key.Up, KeyModifiers.None, () => Move( -1 ) ),
						new( Key.Escape, KeyModifiers.None, UpdateClose ),
					],
				},
				new Text { Content = count.ToString(), FontSize = SmallSize, Color = theme.Muted, FlexShrink = 0 },
			],
		};
	}

	/// <summary>Builds the scrolling list: section headings with column labels, then one row per model.</summary>
	private Container BuildList( IReadOnlyList<PickerRow> rows, int highlight, OmpModelPickerInput input )
	{
		var theme = Theme.Current;
		var children = new List<Blob>();
		if ( !_isLoaded.Value )
			children.Add( BuildNote( _error.Value.Length > 0 ? _error.Value : "Loading models…" ) );
		else if ( _models.Value.Count == 0 )
			children.Add( BuildNote( $"No models are available for {input.Profile}. Sign in to its Omp account in setup, then open this again." ) );
		else if ( rows.Count == 0 )
			children.Add( BuildNote( "No models match your search." ) );

		string? section = null;
		foreach ( var (index, row) in rows.Index() )
		{
			if ( row.Section != section )
			{
				section = row.Section;
				children.Add( BuildSection( section, rows.Count( item => item.Section == section ), isFirst: index == 0 ) );
			}

			children.Add( BuildRow( index, row.Model, index == highlight, input ) );
		}

		return new Container
		{
			Key = "model-picker-list",
			FlexShrink = 1,
			MinHeight = 0,
			OverflowY = Overflow.Scroll,
			Padding = new Edges( theme.Spacing / 2, theme.Spacing ),
			Gap = 1,
			Children = children,
		};
	}

	/// <summary>Builds a section heading with its count, and the column labels on the first one.</summary>
	private static Row BuildSection( string title, int count, bool isFirst )
	{
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Text { Content = title, FontSize = SmallSize, Color = theme.Muted },
			new Text { Content = count.ToString(), FontSize = SmallSize, Color = theme.Inactive, FlexGrow = 1 },
		};
		if ( isFirst )
		{
			children.Add( BuildColumn( "Ctx", ContextWidth, theme.Muted ) );
			children.Add( BuildColumn( "$/M in · out", PriceWidth, theme.Muted ) );
			children.Add( new Container { Width = CheckWidth, FlexShrink = 0 } );
		}

		return new Row
		{
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Padding = new Edges( isFirst ? theme.Spacing / 2 : theme.Spacing * 1.5, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
			Children = children,
		};
	}

	/// <summary>Builds one model row: provider and model, badges, context window and prices, and a check on the model in use.</summary>
	private Button BuildRow( int index, OmpModelInfo model, bool isHighlighted, OmpModelPickerInput input )
	{
		var theme = Theme.Current;
		var name = new List<Blob>
		{
			new Text( [new TextSpan( model.Name, null! ), new TextSpan( "  " + model.Selector, new Style { Color = theme.Muted } )] )
			{
				Color = theme.Text,
				MinWidth = 0,
				FlexShrink = 1,
				TextWrap = TextWrap.NoWrap,
				TextTrimming = TextTrimming.Ellipsis,
			},
		};
		if ( model.Selector == input.Default )
			name.Add( BuildBadge( "default" ) );

		return new Button
		{
			Key = "model:" + index,
			Handle = isHighlighted ? _highlighted : null,
			AccessibilityName = "Select " + model.Selector,
			Focusable = false,
			Cursor = Cursor.Pointer,
			FlexDirection = FlexDirection.Row,
			AlignItems = AlignItems.Center,
			Gap = theme.Spacing / 2,
			Height = theme.ControlHeight + 4,
			Padding = new Edges( 0, theme.Spacing ),
			BorderRadius = theme.Radius,
			BackgroundColor = isHighlighted ? theme.Hover : Color.Transparent,
			OnPointerEnter = _ => _highlight.Value = index,
			OnClick = () => UpdateUse( model, input ),
			Children =
			[
				new Row { AlignItems = AlignItems.Center, Gap = theme.Spacing, FlexGrow = 1, FlexShrink = 1, MinWidth = 0, Children = name },
				BuildColumn( FormatTokens( model.ContextWindow ), ContextWidth, theme.Muted ),
				BuildColumn( FormatPrice( model ), PriceWidth, theme.Muted ),
				new Container
				{
					Width = CheckWidth,
					FlexShrink = 0,
					AlignItems = AlignItems.FlexEnd,
					Children = model.Selector == input.Current
						?
						[
							new Shape
							{
								Path = Check,
								Fit = ShapeFit.Contain,
								Width = 12,
								Height = 12,
								Stroke = new VectorStroke( 1.5, theme.Success ) { Cap = StrokeCap.Round, Join = StrokeJoin.Round },
							},
						]
						: [],
				},
			],
		};
	}

	/// <summary>Builds the highlighted model's details, then the setup, close, default and use actions.</summary>
	private Container BuildFooter( OmpModelInfo? model, OmpModelPickerInput input )
	{
		var theme = Theme.Current;
		var actions = new List<Blob>();
		if ( input.OpenSetup is { } openSetup )
			actions.Add( BuildAction( "Accounts and setup", () => InvokeInputCallback( openSetup ) ) );
		if ( input.OpenRouting is { } openRouting )
			actions.Add( BuildAction( "Model routing", () => InvokeInputCallback( openRouting ) ) );
		actions.Add( new Container { FlexGrow = 1 } );
		var close = BuildAction( "Close", UpdateClose );
		// The window has its own Close caption button, so screen readers need this one told apart.
		close.AccessibilityName = "Close model picker";
		actions.Add( close );
		if ( model is not null && input.MakeDefault is { } makeDefault && model.Selector != input.Default )
			actions.Add( BuildAction( "Make default", () => InvokeInputCallback( makeDefault, model ) ) );
		if ( model is not null )
		{
			var use = BuildAction( input.UseLabel, () => UpdateUse( model, input ) );
			use.BackgroundColor = theme.Accent;
			use.Color = theme.Panel;
			use.HoverStyle = new Style { BackgroundColor = theme.Accent.WithAlpha( 0.85 ) };
			use.FocusStyle = new Style { BackgroundColor = theme.Accent.WithAlpha( 0.85 ) };
			actions.Add( use );
		}

		var children = new List<Blob>();
		if ( model is not null )
			children.Add( new Text { Content = FormatDetails( model ), FontSize = SmallSize, Color = theme.Muted, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis, MinWidth = 0 } );
		children.Add( new Row { AlignItems = AlignItems.Center, Gap = theme.Spacing, Children = actions } );
		return new Container
		{
			Key = "model-picker-footer",
			Gap = theme.Spacing,
			Padding = new Edges( theme.Spacing * 1.5, theme.Spacing * 2 ),
			BorderWidth = new Edges( theme.BorderWidth, 0, 0, 0 ),
			BorderColor = theme.Border,
			Children = children,
		};
	}

	/// <summary>Lists recent models first, then every model, each filtered by the search words.</summary>
	private List<PickerRow> ComputeRows( OmpModelPickerInput input )
	{
		var rows = new List<PickerRow>();
		if ( _models.Value is not { Count: > 0 } models )
			return rows;

		var words = _query.Value.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		bool IsMatch( OmpModelInfo model ) => words.All( word => model.Selector.Contains( word, StringComparison.OrdinalIgnoreCase ) || model.Name.Contains( word, StringComparison.OrdinalIgnoreCase ) );
		var bySelector = models.ToDictionary( model => model.Selector );
		IEnumerable<string> recent = [input.Current, input.Default, .. input.Service.GetRecentModels( input.Profile )];
		foreach ( var selector in recent.Where( selector => selector.Length > 0 ).Distinct() )
		{
			if ( bySelector.TryGetValue( selector, out var model ) && IsMatch( model ) )
				rows.Add( new( "Recent", model ) );
		}

		rows.AddRange( models.Where( IsMatch ).Select( model => new PickerRow( "All", model ) ) );
		return rows;
	}

	/// <summary>Lists the profile's models off the UI thread and shows them; a failure only shows when there is nothing to show instead.</summary>
	private async Task UpdateModelsAsync()
	{
		var input = Input;
		try
		{
			var models = await input.Service.GetModelsAsync( input.Profile, Lifetime ).ConfigureAwait( false );
			Post( () => UpdateModels( models ) );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			Post( () => _error.Value = "Could not list models: " + error.Message );
		}
	}

	/// <summary>Shows a listed catalog.</summary>
	private void UpdateModels( IReadOnlyList<OmpModelInfo> models )
	{
		_models.Value = models;
		_isLoaded.Value = true;
	}

	/// <summary>Remembers the chosen model as recent, then applies it with the main action.</summary>
	private void UpdateUse( OmpModelInfo model, OmpModelPickerInput input )
	{
		input.Service.RecordRecentModel( input.Profile, model.Selector );
		InvokeInputCallback( input.Use, model );
	}

	/// <summary>Closes the picker.</summary>
	private void UpdateClose() => InvokeInputCallback( Input.Close );

	/// <summary>Builds a muted line in place of the list.</summary>
	private static Text BuildNote( string text ) => new()
	{
		Content = text,
		Color = Theme.Current.Muted,
		Padding = new Edges( Theme.Current.Spacing * 2, Theme.Current.Spacing ),
	};

	/// <summary>Builds a right-aligned metadata column of fixed width.</summary>
	private static Text BuildColumn( string text, int width, Color color ) => new()
	{
		Content = text,
		Width = width,
		FlexShrink = 0,
		TextAlign = TextAlign.Right,
		FontSize = SmallSize,
		Color = color,
		TextWrap = TextWrap.NoWrap,
	};

	/// <summary>Builds an outlined label such as "default".</summary>
	private static Container BuildBadge( string text ) => new()
	{
		FlexShrink = 0,
		Padding = new Edges( 0, 6 ),
		BorderRadius = 4,
		BorderWidth = Theme.Current.BorderWidth,
		BorderColor = Theme.Current.Border,
		Children = [new Text { Content = text, FontSize = SmallSize - 1, Color = Theme.Current.Muted }],
	};

	/// <summary>Builds a compact footer action.</summary>
	private static Button BuildAction( string label, Action action )
	{
		var button = Theme.Current.BuildButton( label, action );
		button.Padding = new Edges( 0, Theme.Current.Spacing * 1.5 );
		button.BorderRadius = Theme.Current.ControlHeight / 2.0;
		return button;
	}

	/// <summary>Formats a token count as "1M", "272k", or nothing when unknown.</summary>
	internal static string FormatTokens( long tokens ) => tokens switch
	{
		<= 0 => "",
		>= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
		>= 1_000 => $"{tokens / 1_000.0:0}k",
		_ => tokens.ToString(),
	};

	/// <summary>Formats the price of a million input and output tokens, "free", or nothing when unknown.</summary>
	internal static string FormatPrice( OmpModelInfo model ) => (model.InputCost, model.OutputCost) switch
	{
		(null, null) => "",
		(0 or null, 0 or null) => "free",
		var (input, output) => $"${input ?? 0:0.##} · ${output ?? 0:0.##}",
	};

	/// <summary>Formats the highlighted model's limits, prices and abilities for the footer.</summary>
	private static string FormatDetails( OmpModelInfo model )
	{
		var parts = new List<string> { model.Selector };
		if ( model.ContextWindow > 0 )
			parts.Add( "ctx " + FormatTokens( model.ContextWindow ) );
		if ( model.MaxOutput > 0 )
			parts.Add( "out " + FormatTokens( model.MaxOutput ) );
		if ( FormatPrice( model ) is { Length: > 0 } price )
			parts.Add( price == "free" ? "free" : $"{price} per M" );
		parts.Add( "reasoning " + (model.IsReasoning ? "yes" : "no") );
		parts.Add( "images " + (model.HasImages ? "yes" : "no") );
		return string.Join( "  ·  ", parts );
	}

	/// <summary>Places one model in a section of the list.</summary>
	/// <param name="Section">The section heading, "Recent" or "All".</param>
	/// <param name="Model">The model shown.</param>
	private readonly record struct PickerRow( string Section, OmpModelInfo Model );
}
