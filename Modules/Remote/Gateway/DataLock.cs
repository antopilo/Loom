namespace Lokn.Modules.Remote;

/// <summary>The single-writer lock of a data folder's chats: the daemon and Loom in the <c>local</c> role each hold <c>chats.lock</c> open
/// without sharing while they run, so only one of them writes the conversation history (docs/remote.md, Roles).</summary>
/// <remarks>The file stays; the lock is the open handle, which Windows releases when the process ends, even when it crashes.</remarks>
internal sealed class DataLock : IDisposable
{
	/// <summary>Defines the lock file's name in the data folder.</summary>
	internal const string FileName = "chats.lock";

	/// <summary>Stores the open lock file.</summary>
	private readonly FileStream _file;

	/// <summary>Wraps an open lock file.</summary>
	private DataLock( FileStream file ) => _file = file;

	/// <summary>Takes the lock of a data folder, or answers null when another process holds it.</summary>
	/// <param name="folder">The data folder.</param>
	/// <exception cref="UnauthorizedAccessException">The folder cannot be written.</exception>
	public static DataLock? TryTake( string folder )
	{
		Directory.CreateDirectory( folder );
		try
		{
			var file = new FileStream( Path.Combine( folder, FileName ), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 );
			file.SetLength( 0 );
			file.Write( System.Text.Encoding.UTF8.GetBytes( Environment.ProcessId.ToString( System.Globalization.CultureInfo.InvariantCulture ) ) );
			file.Flush();
			return new DataLock( file );
		}
		catch ( IOException )
		{
			return null;
		}
	}

	/// <summary>Releases the lock.</summary>
	public void Dispose() => _file.Dispose();
}
