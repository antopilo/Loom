using Lokn.Core;

namespace Lokn.Modules.Worktrees;

/// <summary>Runs git and file work off the UI thread and applies the results through the panel's dispatcher.</summary>
internal sealed partial class WorktreesContent
{
	/// <summary>Counts list requests so results from a superseded repository or refresh are dropped.</summary>
	private int _generation;

	/// <summary>Switches to another repository directory and lists it.</summary>
	private void UpdateRepository( string directory )
	{
		_repository.Value = directory;
		_status.Value = "";
		_isSetupOpen.Value = false;
		_ = UpdateViewAsync( resetForm: true );
	}

	/// <summary>Lists the repository's working trees, branches and native builds, then measures each working tree in the background.</summary>
	/// <param name="resetForm">Whether the folders field takes the repository's default folders, as after switching repositories.</param>
	private async Task UpdateViewAsync( bool resetForm )
	{
		var repository = _repository.Value;
		var generation = ++_generation;
		_confirming.Value = "";
		_isBusy.Value = true;
		try
		{
			var view = await WorktreeSharing.ReadViewAsync( repository, _setup, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				if ( generation != _generation )
					return;

				View = view;
				_sizes.Value = [];
				_isBusy.Value = false;
				if ( resetForm )
					_folders.Value = string.Join( ", ", view.Setup.SparseDirectories );
				if ( _setup.LastError is { } error )
					_status.Value = error;
			} );

			// Measuring a large checkout takes far longer than listing, so actions stay available meanwhile.
			foreach ( var worktree in view.Worktrees )
			{
				var excluded = view.Details.GetValueOrDefault( worktree.Path )?.SharedNative;
				var size = await Task.Run( () => GitWorktrees.ComputeSize( worktree.Path, excluded, _context.Lifetime ) ).ConfigureAwait( false );
				_context.Post( () =>
				{
					if ( generation != _generation )
						return;

					_sizes.Value = new( _sizes.Value )
					{
						[worktree.Path] = size,
					};
				} );
			}
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			_context.Post( () =>
			{
				if ( generation != _generation )
					return;

				View = null;
				_sizes.Value = [];
				_isBusy.Value = false;
				_status.Value = error.Message;
			} );
		}
	}

	/// <summary>Opens or closes the setup form, filling it from the repository's saved setup.</summary>
	private void UpdateSetupOpen()
	{
		if ( _isSetupOpen.Value || View is not { } view )
		{
			_isSetupOpen.Value = false;
			return;
		}

		_setupFolders.Value = string.Join( ", ", view.Setup.SparseDirectories );
		_setupShared.Value = string.Join( ", ", view.Setup.SharedFolders );
		_setupInputs.Value = string.Join( ", ", view.Setup.NativeInputs );
		_setupOutput.Value = view.Setup.NativeOutput;
		_isSetupOpen.Value = true;
	}

	/// <summary>Saves the edited setup for the shown repository and lists it again.</summary>
	private void UpdateSaveSetup()
	{
		if ( View is not { } view )
			return;

		var outputs = GitWorktrees.ParseDirectories( _setupOutput.Value );
		if ( outputs.Count > 1 )
		{
			_status.Value = "Give one native output folder.";
			return;
		}

		var setup = new RepositorySetup(
			[.. GitWorktrees.ParseDirectories( _setupFolders.Value )],
			[.. GitWorktrees.ParseDirectories( _setupShared.Value )],
			[.. GitWorktrees.ParseDirectories( _setupInputs.Value )],
			outputs.FirstOrDefault() ?? "" );
		if ( setup.SharedFolders.Contains( setup.NativeOutput, StringComparer.OrdinalIgnoreCase ) )
		{
			_status.Value = $"{setup.NativeOutput} is shared by native sources only; remove it from the shared folders.";
			return;
		}

		_setup.SetSetup( view.MainPath, setup );
		_isSetupOpen.Value = false;
		_status.Value = _setup.LastError ?? "Saved the repository setup. It applies to worktrees created from now on.";
		_ = UpdateViewAsync( resetForm: true );
	}

	/// <summary>Creates a working tree from the typed branch, start point and folders.</summary>
	private void UpdateCreateTyped() => UpdateCreate( _branch.Value.Trim(), _startPoint.Value.Trim() );

	/// <summary>Creates a working tree for a branch with the typed folders, then shares what the setup allows.</summary>
	private void UpdateCreate( string branch, string startPoint )
	{
		if ( _isBusy.Value || branch.Length == 0 || View is not { } view )
			return;

		var folders = GitWorktrees.ParseDirectories( _folders.Value );
		_ = UpdateGitAsync( async () =>
		{
			var (path, messages) = await WorktreeSharing.CreateAsync( _setup, view.MainPath, branch, startPoint, folders, _context.Lifetime ).ConfigureAwait( false );
			return string.Join( " ", [$"Created {path}.", .. messages] );
		}, () =>
		{
			_branch.Value = "";
			_startPoint.Value = "";
			_changed();
		} );
	}

	/// <summary>Arms removal on the first click and removes the working tree, after its folder links, on the confirming second click; chats working in it move back to the main checkout.</summary>
	private void UpdateRemove( string path )
	{
		if ( _isBusy.Value || UpdateConfirming( path, "Removing deletes this working tree's directory. Its branch is kept, and shared folders and builds stay in the checkouts that own them. Choose Confirm remove to continue." ) )
			return;

		var repository = _repository.Value;
		var main = View?.MainPath ?? repository;
		_ = UpdateGitAsync( async () =>
		{
			await WorktreeSharing.RemoveAsync( repository, path, _context.Lifetime ).ConfigureAwait( false );
			_context.Post( () =>
			{
				_setup.Forget( path );
				_store.ReleaseWorktree( path, main );
				_changed();
			} );
			return $"Removed {path}.";
		} );
	}

	/// <summary>Arms deletion on the first click and deletes the local branch on the confirming second click; git refuses unmerged work.</summary>
	private void UpdateDeleteBranch( BranchInfo branch )
	{
		if ( _isBusy.Value || UpdateConfirming( branch.Reference, $"Deleting removes the local branch {branch.Name}. Git refuses when it has commits that are not merged. Choose Confirm delete to continue." ) )
			return;

		var repository = _repository.Value;
		_ = UpdateGitAsync( async () =>
		{
			await GitWorktrees.DeleteBranchAsync( repository, branch.Name, _context.Lifetime ).ConfigureAwait( false );
			return $"Deleted branch {branch.Name}.";
		} );
	}

	/// <summary>Fetches every remote so remote branches are current.</summary>
	private void UpdateFetch()
	{
		if ( _isBusy.Value )
			return;

		var repository = _repository.Value;
		_ = UpdateGitAsync( async () =>
		{
			await GitWorktrees.FetchAsync( repository, _context.Lifetime ).ConfigureAwait( false );
			return "Fetched every remote.";
		} );
	}

	/// <summary>Records, shares or stops sharing a working tree's native build.</summary>
	private void UpdateNative( string path, NativeAction action )
	{
		if ( _isBusy.Value || View is not { } view )
			return;

		var snapshot = _setup.Capture( view.MainPath );
		_ = UpdateGitAsync( async () =>
		{
			switch ( action )
			{
				case NativeAction.Record:
					var record = await WorktreeSharing.RecordAsync( path, snapshot.Setup, _context.Lifetime ).ConfigureAwait( false );
					_context.Post( () => _setup.SetBuild( path, record ) );
					return $"Recorded the native build of {Path.GetFileName( path )} ({record.Fingerprint[..NativeBuilds.ShortLength]}).";

				case NativeAction.Share:
					var (link, count) = await WorktreeSharing.ShareNativeAsync( path, snapshot, _context.Lifetime ).ConfigureAwait( false );
					_context.Post( () => _setup.SetLink( path, link ) );
					return $"{Path.GetFileName( path )} shares the native build of {Path.GetFileName( link.Source )} ({count} files). Build managed only there.";

				default:
					var deleted = await WorktreeSharing.StopSharingNativeAsync( path, snapshot.Setup, _context.Lifetime ).ConfigureAwait( false );
					_context.Post( () => _setup.SetLink( path, null ) );
					return $"{Path.GetFileName( path )} no longer shares a native build ({deleted} linked files removed). Build native there before running it.";
			}
		} );
	}

	/// <summary>Arms a destructive action on its first click and reports whether it still waits for the confirming click.</summary>
	private bool UpdateConfirming( string key, string message )
	{
		if ( _confirming.Value == key )
			return false;

		_confirming.Value = key;
		_status.Value = message;
		return true;
	}

	/// <summary>Runs one changing command, reports its result or error, and lists the repository again.</summary>
	private async Task UpdateGitAsync( Func<Task<string>> command, Action? onSucceeded = null )
	{
		_isBusy.Value = true;
		string status;
		var isSucceeded = false;
		try
		{
			status = await Task.Run( command ).ConfigureAwait( false );
			isSucceeded = true;
		}
		catch ( Exception error ) when ( error is not OperationCanceledException )
		{
			status = error.Message;
		}

		_context.Post( () =>
		{
			if ( isSucceeded )
				onSucceeded?.Invoke();

			_status.Value = _setup.LastError ?? status;
			_ = UpdateViewAsync( resetForm: false );
		} );
	}

	/// <summary>Opens a worktree's folder in the file manager if it exists.</summary>
	private static void UpdateOpen( string path )
	{
		if ( Directory.Exists( path ) )
			FileManager.Open( path );
	}
}
