using System.Text.Json;
using Goo;
using Lokn.Core;

namespace Lokn.Modules.Git;

/// <summary>The Files side view's Changes or History mode: the Git views, laid out narrow, on the repository holding the side view's folder.</summary>
/// <remarks>When the side view shows another folder, such as after the user opens another project, the views close and open again on it.</remarks>
/// <param name="context">The side view's panel, tree and the mode's restored state.</param>
/// <param name="layout">The narrow layout: <see cref="GitLayout.Changes"/> or <see cref="GitLayout.History"/>.</param>
internal sealed class GitSideMode( FileTreeModeContext context, GitLayout layout ) : PanelContent
{
	/// <summary>Stores the views on the current folder, or null before the first build.</summary>
	private GitContent? _content;

	/// <summary>Stores the folder <see cref="_content"/> shows.</summary>
	private string? _root;

	/// <summary>Stores the review the mode linked before the panel last closed, until the first views pick it up.</summary>
	private GitPanelInput? _restored = context.RestoredState?.Deserialize<GitPanelInput>();

	/// <summary>Builds the views on the side view's folder, opening them again when it changed.</summary>
	public override Blob Build()
	{
		var root = context.Tree.Root;
		if ( root.Length == 0 )
			return new Text { Content = "Open a project to see its repository.", Color = Theme.Current.Muted, Padding = Theme.Current.Spacing * 2 };

		if ( _content is null || !string.Equals( root, _root, StringComparison.OrdinalIgnoreCase ) )
		{
			_content?.Closed();
			_root = root;
			_content = new GitContent( context.Panel, new GitPanelInput( root, _restored?.ReviewAction, _restored?.ReviewId ), layout );
			_restored = null;
		}

		return _content.Build();
	}

	/// <summary>Keeps the linked review, so the mode shows it again after a restart.</summary>
	public override JsonElement? CaptureState() => _content?.CaptureState() ?? context.RestoredState;

	/// <summary>Stops the views' reads, command and git watch.</summary>
	public override void Closed() => _content?.Closed();
}
