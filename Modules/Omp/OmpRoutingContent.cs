using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Omp;

/// <summary>Edits Omp model roles, fallback chains and named presets for one Lokn account profile.</summary>
internal sealed class OmpRoutingContent : PanelContent
{
	/// <summary>Defines the narrowest a selector column gets before the routing table wraps it under the role.</summary>
	private const int RouteEntryMinWidth = 120;

	/// <summary>Defines built-in Omp roles in their useful display order.</summary>
	private static readonly string[] BuiltInRoles =
	[
		"default", "smol", "slow", "plan", "task", "advisor", "commit", "vision", "tiny", "memory", "widget",
		"web", "image", "speech", "dictation", "judge",
	];

	/// <summary>Defines supported persistent default reasoning levels.</summary>
	private static readonly string[] ThinkingLevels = ["minimal", "low", "medium", "high", "xhigh", "max", "auto"];

	/// <summary>Stores the owning panel's dispatch and lifetime.</summary>
	private readonly PanelContext _context;

	/// <summary>Runs profile configuration commands.</summary>
	private readonly OmpService _service;

	/// <summary>Rebuilds after asynchronous and editor changes.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Stores editable primary selectors by role.</summary>
	private readonly Dictionary<string, string> _roles = new( StringComparer.Ordinal );

	/// <summary>Stores editable comma-separated fallback selectors by role.</summary>
	private readonly Dictionary<string, string> _fallbacks = new( StringComparer.Ordinal );

	/// <summary>Stores the loaded named presets.</summary>
	private Dictionary<string, OmpModelPreset> _presets = new( StringComparer.Ordinal );

	/// <summary>Stores the profile whose routing is shown.</summary>
	private SettingsProfile _profile;

	/// <summary>Stores the editable default reasoning level.</summary>
	private string _thinking = "high";

	/// <summary>Stores the name typed for a new preset.</summary>
	private string _presetName = "";

	/// <summary>Reports whether configuration is loading or saving.</summary>
	private bool _isBusy;

	/// <summary>Stores the visible operation status or error.</summary>
	private string _status = "Loading routing…";

	/// <summary>Creates a routing editor and starts its first read.</summary>
	internal OmpRoutingContent( PanelContext context, OmpService service, SettingsProfile profile )
	{
		_context = context;
		_service = service;
		_profile = profile;
		_isBusy = true;
		_ = LoadAsync();
	}

	/// <summary>Builds profile controls, preset actions and the role routing table.</summary>
	public override Blob Build()
	{
		_ = _version.Value;
		var theme = Theme.Current;
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				FlexWrap = FlexWrap.Wrap,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Model routing", FontSize = theme.FontSize + 4, FontWeight = 600, FlexGrow = 1 },
					BuildProfileButton( SettingsProfile.Work ),
					BuildProfileButton( SettingsProfile.Personal ),
					theme.BuildButton( _isBusy ? "Working…" : "Refresh", () => _context.Run( StartLoad ), !_isBusy ),
				],
			},
			new Text
			{
				Content = "Omp owns these routes. Changes affect new sessions and later subagent or fallback decisions; an open chat keeps its active model until you switch it.",
				Color = theme.Muted,
			},
			BuildPresets(),
			BuildRoutingTable(),
			new Row
			{
				AlignItems = AlignItems.Center,
				FlexWrap = FlexWrap.Wrap,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = _status, Color = _status.StartsWith( "Could not", StringComparison.Ordinal ) ? theme.Error : theme.Muted, FlexGrow = 1, Selectable = true },
					theme.BuildButton( "Save routing", () => _context.Run( StartSave ), !_isBusy ),
				],
			},
		};
		return new Container
		{
			FlexGrow = 1,
			MinWidth = 0,
			MinHeight = 0,
			Padding = theme.Spacing * 2,
			Gap = theme.Spacing * 1.5,
			Color = theme.Text,
			FontFamily = theme.FontFamily,
			FontSize = theme.FontSize,
			OverflowY = Overflow.Scroll,
			ScrollbarY = ViewStyle.BuildScrollbar(),
			Children = children,
		};
	}

	/// <summary>Keeps the selected profile when the workspace layout is restored.</summary>
	public override JsonElement? CaptureState() => JsonSerializer.SerializeToElement( _profile );

	/// <summary>Changes the shown profile and reloads its independent Omp configuration.</summary>
	internal void UpdateProfile( SettingsProfile profile )
	{
		if ( _profile == profile )
			return;
		_profile = profile;
		_isBusy = true;
		_status = "Loading routing…";
		MarkChanged();
		_ = LoadAsync();
	}

	/// <summary>Builds a profile tab button.</summary>
	private Button BuildProfileButton( SettingsProfile profile )
	{
		var button = Theme.Current.BuildButton( profile.ToString(), () => _context.Run( () => UpdateProfile( profile ) ), !_isBusy );
		if ( profile == _profile )
			button.BackgroundColor = Theme.Current.Accent.WithAlpha( 0.2 );
		return button;
	}

	/// <summary>Builds named preset apply, delete and save controls.</summary>
	private Container BuildPresets()
	{
		var theme = Theme.Current;
		var rows = new List<Blob>
		{
			new Text { Content = "Presets", FontWeight = 600 },
		};
		foreach ( var preset in _presets.OrderBy( item => item.Key, StringComparer.OrdinalIgnoreCase ) )
		{
			var item = preset;
			rows.Add( new Row
			{
				AlignItems = AlignItems.Center,
				FlexWrap = FlexWrap.Wrap,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = item.Key, FontWeight = 600, Width = 150, FlexShrink = 0 },
					new Text { Content = $"{item.Value.Roles.Count:N0} roles · {item.Value.DefaultThinkingLevel}", Color = theme.Muted, FlexGrow = 1 },
					theme.BuildButton( "Apply", () => _context.Run( () => StartApplyPreset( item.Key, item.Value ) ), !_isBusy ),
					theme.BuildButton( "Delete", () => _context.Run( () => StartDeletePreset( item.Key ) ), !_isBusy ),
				],
			} );
		}
		rows.Add( new Row
		{
			AlignItems = AlignItems.Center,
			FlexWrap = FlexWrap.Wrap,
			Gap = theme.Spacing,
			Children =
			[
				new TextEntry
				{
					Value = _presetName,
					Controlled = true,
					Placeholder = "Preset name",
					AccessibilityName = "Preset name",
					FlexBasis = 220,
					MinWidth = 0,
					OnChange = value =>
					{
						_presetName = value;
						MarkChanged();
					},
				},
				theme.BuildButton( "Save current as preset", () => _context.Run( StartSavePreset ), !_isBusy && _presetName.Trim().Length > 0 ),
			],
		} );
		return new Container
		{
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Raised,
			Children = rows,
		};
	}

	/// <summary>Builds the reasoning-level control and one editable row per built-in or configured role.</summary>
	private Container BuildRoutingTable()
	{
		var theme = Theme.Current;
		var roles = BuiltInRoles.Concat( _roles.Keys ).Concat( _fallbacks.Keys ).Distinct( StringComparer.Ordinal ).ToArray();
		var rows = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				FlexWrap = FlexWrap.Wrap,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = "Roles and fallbacks", FontWeight = 600, FlexGrow = 1 },
					new Text { Content = "Default thinking", Color = theme.Muted },
					theme.BuildButton( _thinking, () => _context.Run( UpdateThinking ), !_isBusy ),
				],
			},
			new Row
			{
				Gap = theme.Spacing,
				FlexWrap = FlexWrap.Wrap,
				Children =
				[
					new Text { Content = "Role", Width = 100, Color = theme.Muted, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
					new Row
					{
						FlexGrow = 1,
						FlexBasis = 0,
						MinWidth = RouteEntryMinWidth,
						FlexWrap = FlexWrap.Wrap,
						Gap = theme.Spacing,
						Children =
						[
							new Text { Content = "Primary selector", FlexGrow = 1, FlexBasis = 0, MinWidth = RouteEntryMinWidth, Color = theme.Muted, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
							new Text { Content = "Fallback selectors, in order", FlexGrow = 1, FlexBasis = 0, MinWidth = RouteEntryMinWidth, Color = theme.Muted, TextWrap = TextWrap.NoWrap, TextTrimming = TextTrimming.Ellipsis },
						],
					},
				],
			},
		};
		foreach ( var role in roles )
		{
			_roles.TryAdd( role, "" );
			_fallbacks.TryAdd( role, "" );
			var name = role;
			rows.Add( new Row
			{
				AlignItems = AlignItems.Center,
				FlexWrap = FlexWrap.Wrap,
				Gap = theme.Spacing,
				Children =
				[
					new Text { Content = name, Width = 100, FlexShrink = 0 },
					new Row
					{
						FlexGrow = 1,
						FlexBasis = 0,
						MinWidth = RouteEntryMinWidth,
						FlexWrap = FlexWrap.Wrap,
						Gap = theme.Spacing,
						Children = [BuildRouteEntry( name, isFallback: false ), BuildRouteEntry( name, isFallback: true )],
					},
				],
			} );
		}
		return new Container
		{
			Padding = theme.Spacing * 1.5,
			Gap = theme.Spacing / 2,
			BorderWidth = theme.BorderWidth,
			BorderColor = theme.Border,
			BorderRadius = theme.Radius,
			Children = rows,
		};
	}

	/// <summary>Builds one controlled primary-selector or fallback-list editor.</summary>
	private TextEntry BuildRouteEntry( string role, bool isFallback ) => new()
	{
		Value = isFallback ? _fallbacks[role] : _roles[role],
		Controlled = true,
		Placeholder = isFallback ? "provider/model, @default" : "provider/model:effort",
		AccessibilityName = $"{role} {(isFallback ? "fallbacks" : "primary model")}",
		FlexGrow = 1,
		FlexBasis = 0,
		MinWidth = RouteEntryMinWidth,
		OnChange = value =>
		{
			if ( isFallback )
				_fallbacks[role] = value;
			else
				_roles[role] = value;
		},
	};

	/// <summary>Cycles through Omp's persistent default reasoning levels.</summary>
	private void UpdateThinking()
	{
		var index = Array.IndexOf( ThinkingLevels, _thinking );
		_thinking = ThinkingLevels[(index + 1 + ThinkingLevels.Length) % ThinkingLevels.Length];
		MarkChanged();
	}

	/// <summary>Starts a fresh configuration read.</summary>
	private void StartLoad()
	{
		if ( _isBusy )
			return;
		_isBusy = true;
		_status = "Loading routing…";
		MarkChanged();
		_ = LoadAsync();
	}

	/// <summary>Reads routing off the UI thread and applies it to the editor.</summary>
	private async Task LoadAsync()
	{
		var profile = _profile;
		try
		{
			var routing = await _service.GetModelRoutingAsync( profile, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				if ( _profile != profile )
					return;
				_roles.Clear();
				foreach ( var item in routing.Roles )
					_roles[item.Key] = item.Value;
				_fallbacks.Clear();
				foreach ( var item in routing.Fallbacks )
					_fallbacks[item.Key] = item.Value.Count == 0 ? "[]" : string.Join( ", ", item.Value );
				_presets = new( routing.Presets, StringComparer.Ordinal );
				_thinking = routing.DefaultThinkingLevel;
				_isBusy = false;
				_status = $"{profile} routing loaded.";
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not load routing: " + error.Message ) );
		}
	}

	/// <summary>Starts saving the edited roles and fallback chains.</summary>
	private void StartSave()
	{
		if ( _isBusy )
			return;
		_isBusy = true;
		_status = "Saving routing…";
		MarkChanged();
		_ = SaveAsync();
	}

	/// <summary>Normalizes and writes the current role and fallback editors.</summary>
	private async Task SaveAsync()
	{
		var roles = _roles.Where( item => item.Value.Trim().Length > 0 ).ToDictionary( item => item.Key, item => item.Value.Trim(), StringComparer.Ordinal );
		var fallbacks = _fallbacks.Where( item => item.Value.Trim().Length > 0 ).ToDictionary(
			item => item.Key,
			item => (IReadOnlyList<string>)( item.Value.Trim() == "[]" ? [] : item.Value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) ),
			StringComparer.Ordinal );
		try
		{
			await _service.SaveModelRoutingAsync( _profile, roles, fallbacks, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_isBusy = false;
				_status = "Routing saved. Open chats keep their current model.";
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not save routing: " + error.Message ) );
		}
	}

	/// <summary>Saves the current edited roles as one named Omp preset.</summary>
	private void StartSavePreset()
	{
		var name = _presetName.Trim();
		if ( _isBusy || name.Length == 0 )
			return;
		var roles = _roles.Where( item => item.Value.Trim().Length > 0 ).ToDictionary( item => item.Key, item => item.Value.Trim(), StringComparer.Ordinal );
		var presets = new Dictionary<string, OmpModelPreset>( _presets, StringComparer.Ordinal )
		{
			[name] = new( roles, _thinking ),
		};
		_isBusy = true;
		_status = $"Saving {name}…";
		MarkChanged();
		_ = SavePresetsAsync( presets, $"Preset {name} saved." );
	}

	/// <summary>Applies one loaded preset to Omp's active routing configuration.</summary>
	private void StartApplyPreset( string name, OmpModelPreset preset )
	{
		if ( _isBusy )
			return;
		_isBusy = true;
		_status = $"Applying {name}…";
		MarkChanged();
		_ = ApplyPresetAsync( name, preset );
	}

	/// <summary>Applies a preset and reloads the effective configuration.</summary>
	private async Task ApplyPresetAsync( string name, OmpModelPreset preset )
	{
		try
		{
			await _service.ApplyModelPresetAsync( _profile, preset, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_isBusy = false;
				_status = $"Preset {name} applied.";
				StartLoad();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not apply preset: " + error.Message ) );
		}
	}

	/// <summary>Deletes one preset from Omp's profile configuration.</summary>
	private void StartDeletePreset( string name )
	{
		if ( _isBusy )
			return;
		var presets = new Dictionary<string, OmpModelPreset>( _presets, StringComparer.Ordinal );
		presets.Remove( name );
		_isBusy = true;
		_status = $"Deleting {name}…";
		MarkChanged();
		_ = SavePresetsAsync( presets, $"Preset {name} deleted." );
	}

	/// <summary>Writes the complete preset record, preserving every entry not changed by this editor.</summary>
	private async Task SavePresetsAsync( Dictionary<string, OmpModelPreset> presets, string success )
	{
		try
		{
			await _service.SaveModelPresetAsync( _profile, presets, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_presets = presets;
				_presetName = "";
				_isBusy = false;
				_status = success;
				MarkChanged();
			} );
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () => FinishError( "Could not save presets: " + error.Message ) );
		}
	}

	/// <summary>Ends a failed operation and shows its actionable diagnostic.</summary>
	private void FinishError( string message )
	{
		_isBusy = false;
		_status = message;
		MarkChanged();
	}

	/// <summary>Rebuilds after mutable panel state changes.</summary>
	private void MarkChanged() => _version.Value++;
}
