using Goo;
using Lokn.Core;
using Lokn.Modules.Projects;

namespace Lokn.Modules.Conversations;

/// <summary>Asks the user, in a dialog, to confirm tidying a project's chats, naming the chats it would remove.</summary>
internal sealed partial class HistoryContent
{
	/// <summary>Defines how many chats to be removed the confirmation names before counting the rest.</summary>
	private const int TidyListLimit = 6;

	/// <summary>Works out what tidying a project would do and asks the user to confirm it; asking again for the same project closes the question.</summary>
	private void BeginTidy( ProjectInfo project )
	{
		_sidebar.CloseMenu();
		_tidyPlan.Value = _tidyPlan.Value.ProjectId == project.Id ? TidyPlan.None : module.ComputeTidy( context.App, project.Id, DateTimeOffset.UtcNow );
	}

	/// <summary>Builds the confirmation dialog: how many chats are renamed, which are removed, and Tidy and Cancel; only Close when there is nothing to do.</summary>
	private Blob BuildTidy( ProjectInfo project, TidyPlan plan )
	{
		var lines = new List<string>();
		var items = new List<string>();
		var isTidying = module.IsTidying( project.Id );
		if ( isTidying )
			lines.Add( "Already naming this project's chats." );
		else if ( plan.Stale.Count == 0 && plan.Renamed.Count == 0 )
			lines.Add( "Nothing to tidy." );
		else
		{
			if ( plan.Renamed.Count > 0 )
				lines.Add( plan.Renamed.Count == 1 ? "Rename 1 chat for what it is about now." : $"Rename {plan.Renamed.Count} chats for what they are about now." );
			if ( plan.Stale.Count > 0 )
			{
				lines.Add( $"Remove {(plan.Stale.Count == 1 ? "1 chat" : $"{plan.Stale.Count} chats")} never started or quiet for {ConversationsModule.StaleAge.Days} days:" );
				items.AddRange( plan.Stale.Take( TidyListLimit ).Select( conversation => conversation.Title ) );
				if ( plan.Stale.Count > TidyListLimit )
					items.Add( $"and {plan.Stale.Count - TidyListLimit} more" );
			}
		}

		var canTidy = !isTidying && (plan.Stale.Count > 0 || plan.Renamed.Count > 0);
		return Cell.Mount<ConfirmDialogInput, ConfirmDialog>( "tidy", new( context.App, $"Tidy chats in {project.Name}", string.Join( "\n", lines ), canTidy ? "Tidy" : null,
			() => context.Run( () => ConfirmTidy( plan ) ), () => context.Run( () => _tidyPlan.Value = TidyPlan.None ) )
		{
			Items = items,
			IsDestructive = plan.Stale.Count > 0,
		} );
	}

	/// <summary>Tidies the project as confirmed and closes the question.</summary>
	private void ConfirmTidy( TidyPlan plan )
	{
		_tidyPlan.Value = TidyPlan.None;
		module.Tidy( context.App, plan );
	}
}
