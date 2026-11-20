namespace Lokn.Modules.Remote;

/// <summary>Describes one action an agent took in a window of this PC, the payload of <c>cap.agent</c>.</summary>
/// <param name="Hwnd">The window's handle.</param>
/// <param name="Kind">What the agent did: <see cref="Capture"/>, <see cref="Click"/>, <see cref="Type"/>, <see cref="Key"/> or <see cref="Scroll"/>.</param>
/// <param name="X">Where, as a fraction of the window's width from its left edge, 0 to 1; negative when the action has no point.</param>
/// <param name="Y">Where, as a fraction of the window's height from its top edge, 0 to 1; negative when the action has no point.</param>
public sealed record AgentWindowUse( long Hwnd, string Kind, double X = -1, double Y = -1 )
{
	/// <summary>Defines the kind of a capture the agent took.</summary>
	public const string Capture = "capture";

	/// <summary>Defines the kind of a click.</summary>
	public const string Click = "click";

	/// <summary>Defines the kind of typed text.</summary>
	public const string Type = "type";

	/// <summary>Defines the kind of a pressed key.</summary>
	public const string Key = "key";

	/// <summary>Defines the kind of a turned wheel.</summary>
	public const string Scroll = "scroll";

	/// <summary>Gets whether the action has a point viewers mark.</summary>
	public bool HasPoint => X >= 0 && Y >= 0;
}

/// <summary>Tells this process's window stream panels that an agent used a window: the agent tools of a local-role Loom raise it, and so does the
/// daemon link of a daemon-role Loom on each <c>cap.agent</c> of its daemon, so <see cref="LocalStreamSource"/> shows the marker.</summary>
/// <remarks>Raised on any thread.</remarks>
public static class AgentWindowActivity
{
	/// <summary>Raised for each action an agent takes in a window of this PC.</summary>
	public static event Action<AgentWindowUse>? Used;

	/// <summary>Raises <see cref="Used"/>.</summary>
	/// <param name="use">The action.</param>
	public static void Raise( AgentWindowUse use ) => Used?.Invoke( use );
}
