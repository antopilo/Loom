using System.Text.Json;
using Goo;
using Lokn.Core;
using Lokn.Modules.Conversations;
using Lokn.Modules.Git;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Chat;

/// <summary>Shows the folder a chat works in and its git state under the prompt, and lets a chat outside projects choose its folder.</summary>
public sealed partial class ChatContent
{
	/// <summary>Watches the git state of the chat's folder while the panel is open, or null without the Git module.</summary>
	private GitWatch? _gitWatch;

	/// <summary>Stores the folder <see cref="_gitWatch"/> watches, as the chat resolved it.</summary>
	private string? _gitDirectory;

	/// <summary>Builds the left of the rail under the prompt: the folder of a chat outside projects, then the branch, how far it is from its upstream, and the lines changed.</summary>
	/// <remarks>A chat outside projects shows its folder as a button that chooses another; a project chat shows no folder, since its project names it.
	/// Outside a repository, or before git answers, only that folder shows.</remarks>
	private List<Blob> BuildFolderRail( ConversationInfo conversation )
	{
		var directory = store.GetDirectory( conversationId );
		var state = _gitWatch?.State;
		var rail = new List<Blob>();
		if ( conversation.ProjectId is null )
		{
			var name = FolderName.Compute( state?.Root is { } root && ConversationStore.IsSameFolder( root, directory ) ? root : directory );
			var choose = BuildRailButton( name, () => context.Run( UpdateChooseFolder ) );
			choose.Key = "conversation-folder";
			choose.AccessibilityName = $"Choose the chat's folder, now {directory}";
			rail.Add( choose );
		}

		if ( state is not { IsRepository: true } )
			return rail;

		if ( (state.Branch ?? state.Commit) is { } head )
		{
			var branch = BuildRailButton( head, () => context.Run( OpenFolderGit ) );
			branch.Key = "conversation-branch";
			branch.AccessibilityName = $"Open Git for {head}";
			branch.FlexShrink = 0;
			branch.Children =
			[
				new Row
				{
					AlignItems = AlignItems.Center,
					Gap = Theme.Current.Spacing / 4,
					Children = [BuildBranchIcon(), BuildRailRolling( "conversation-branch-name", head, Theme.Current.Muted )],
				},
			];
			rail.Add( branch );
		}
		if ( state.Ahead > 0 || state.Behind > 0 )
		{
			var sync = state.Ahead > 0 && state.Behind > 0 ? $"↑{state.Ahead} ↓{state.Behind}" : state.Ahead > 0 ? $"↑{state.Ahead}" : $"↓{state.Behind}";
			rail.Add( new Row
			{
				Key = "conversation-upstream",
				FlexShrink = 0,
				AccessibilityName = $"{state.Ahead} ahead and {state.Behind} behind {state.Upstream ?? "upstream"}",
				Children = [BuildRailRolling( "conversation-upstream-count", sync, Theme.Current.Muted )],
			} );
		}
		if ( state.Added > 0 || state.Removed > 0 )
			rail.Add( BuildChangesButton( state ) );
		return rail;
	}

	/// <summary>Builds the change summary, <c>+added −removed</c>, which opens the folder's diff beside the chat.</summary>
	private Button BuildChangesButton( GitState state )
	{
		var changes = BuildRailButton( "", () => context.Run( OpenFolderGit ) );
		changes.Key = "conversation-changes";
		changes.AccessibilityName = $"Show the diff: {Count( state.Files, "changed file", "changed files" )}, {state.Added:N0} lines added, {state.Removed:N0} removed";
		changes.FlexShrink = 0;
		changes.Children =
		[
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = Theme.Current.Spacing / 2,
				Children =
				[
					BuildRailRolling( "conversation-changes-added", $"+{state.Added:N0}", Theme.Current.Success ),
					BuildRailRolling( "conversation-changes-removed", $"−{state.Removed:N0}", Theme.Current.Error ),
				],
			},
		];
		return changes;
	}

	/// <summary>Builds a small piece of text for the line under the prompt that rolls to its new value when it changes, such as a count.</summary>
	private static Blob BuildRailRolling( string key, string content, Color color ) =>
		Cell.Mount<RollingTextInput, RollingText>( key, new( content, ViewStyle.SmallSize, color ) { FontFamily = ViewStyle.FontFamily, Shrink = false } );

	/// <summary>Defines the branch icon's view box in logical pixels.</summary>
	private const double BranchBox = 12;

	/// <summary>Holds the branch icon: a trunk between two commits and a branch curving off it to a third.</summary>
	/// <remarks>Drawn rather than typed, since the UI font has no branch character and would leave a blank.</remarks>
	private static readonly VectorPath BranchIcon = VectorPath.Parse(
		"M3.5 1.5 A1.25 1.25 0 1 1 3.5 4 A1.25 1.25 0 1 1 3.5 1.5 M3.5 8 A1.25 1.25 0 1 1 3.5 10.5 A1.25 1.25 0 1 1 3.5 8 "
		+ "M8.5 2.5 A1.25 1.25 0 1 1 8.5 5 A1.25 1.25 0 1 1 8.5 2.5 M3.5 4 L3.5 8 M8.5 5 L8.5 5.5 A2 2 0 0 1 6.5 7.5 L3.5 7.5",
		new ElementRect { Width = BranchBox, Height = BranchBox } );

	/// <summary>Builds the branch icon at the rail's text size.</summary>
	private static Shape BuildBranchIcon() => new()
	{
		Path = BranchIcon,
		Fit = ShapeFit.Contain,
		Width = ViewStyle.SmallSize,
		Height = ViewStyle.SmallSize,
		FlexShrink = 0,
		Stroke = new VectorStroke( 1.1, Theme.Current.Muted )
		{
			Cap = StrokeCap.Round,
			Join = StrokeJoin.Round,
		},
	};

	/// <summary>Gets the Git module while it is on, or null; looked up on each use, so turning Git on or off takes effect at once.</summary>
	private GitModule? FindGit() => context.App.Modules.Any( module => module.Id == "git" ) ? context.App.GetModule<GitModule>() : null;

	/// <summary>Opens the Git panel of the chat's folder beside this chat, on its graph and working tree, or brings forward the one already showing its repository.</summary>
	private void OpenFolderGit() => FindGit()?.OpenPanel( context.App, context.Id, store.GetDirectory( conversationId ) );

	/// <summary>Watches the git state of the chat's folder, replacing the watch of the folder it worked in before; without the Git module, nothing is watched.</summary>
	private void UpdateGitWatch( string directory )
	{
		if ( FindGit() is not { } watcher )
		{
			// Git was turned off: its watches ended with it.
			_gitWatch = null;
			_gitDirectory = null;
			return;
		}
		if ( _isClosed || _gitWatch is not null && _gitDirectory == directory )
			return;

		var previous = _gitWatch;
		_gitWatch = watcher.Watch( context.Host, directory );
		_gitDirectory = directory;
		previous?.Dispose();
	}

	/// <summary>Asks for the chat's new folder, unless the chat cannot change its folder now: the native folder chooser over the shell window on This PC,
	/// a typed path checked on the host for a paired host's chat.</summary>
	private void UpdateChooseFolder()
	{
		if ( session.ComputeFolderRefusal( store.Get( conversationId ) ) is { } refusal )
		{
			session.UpdateStatus( refusal );
			return;
		}

		if ( !context.Host.IsLocal )
			HostFolderPrompt.Show( context, context.Host, "Choose the chat's folder", store.GetDirectory( conversationId ), directory => UpdateDirectory( directory ) );
		else if ( context.App.Window is { } window )
			_ = UpdateChooseFolderAsync( window );
	}

	/// <summary>Moves the chat to the chosen folder once the owner-modal chooser closes.</summary>
	private async Task UpdateChooseFolderAsync( Window window )
	{
		var result = await window.ShowFileDialogAsync( FileDialogKind.Folder, new FileDialogOptions
		{
			Title = "Choose the chat's folder",
			InitialPath = store.GetDirectory( conversationId ),
		} );
		context.Post( () =>
		{
			if ( result.Status == FileDialogStatus.Success && result.Paths.FirstOrDefault() is { } directory )
			{
				UpdateDirectory( directory );
			}
			else if ( result.Status != FileDialogStatus.Cancelled )
			{
				session.UpdateStatus( $"Could not open the folder chooser: {result.Error}" );
			}
		} );
	}

	/// <summary>Moves a chat outside projects to another folder. A connected Omp stops, so the next prompt starts Omp in the new folder and continues the same session.</summary>
	/// <returns>Whether the folder was set; a project chat, or one whose agent is working or connecting, keeps its folder and says why.</returns>
	internal bool UpdateDirectory( string directory )
	{
		if ( _isClosed || !session.UpdateDirectory( directory ) )
			return false;

		UpdateGitWatch( store.GetDirectory( conversationId ) );
		MarkChanged();
		return true;
	}
}
