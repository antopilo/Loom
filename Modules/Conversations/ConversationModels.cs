using System.Text.Json;
using Lokn.Core;

namespace Lokn.Modules.Conversations;

/// <summary>Describes a task: a chat with the agent and its own layout, or, without a chat, only a layout of other panels such as notes or the feed.</summary>
/// <param name="Draft">The prompt text typed but not yet accepted by the backend, or null when the composer is empty.</param>
/// <param name="IsPinned">Whether the task stays at the top of its project or the chats outside projects.</param>
/// <param name="IsTitleChosen">Whether the user named the task, so no generated title replaces it.</param>
/// <param name="IsTitleGenerated">Whether the title was already summarized by a model, so it is not summarized again.</param>
/// <param name="CreatedAt">When the task was started, which fixes its place in the sidebar until the user moves a task among it and its neighbors.</param>
/// <param name="HasChat">Whether the task has a chat; one without is only its layout of other panels.</param>
/// <param name="Directory">The folder chosen for a chat outside projects to work in, or null for the application's current directory;
/// a project chat works in its project's directory or <paramref name="Worktree"/>. Resolve it with <see cref="ConversationStore.GetDirectory"/>.</param>
/// <param name="Worktree">The linked git worktree of its project's repository a project chat works in, such as one its agent moved into to work
/// in parallel; null for the project's own directory. Changing the chat's project clears it.</param>
/// <param name="IsTaskCheckOff">Whether the user asked this chat never to suggest a new chat for a prompt that looks like a new task.</param>
/// <param name="Position">The task's place among its project's tasks, or the chats outside projects, once the user dragged one of them to another
/// place; null for a task started since, which lists above the placed ones, newest first. Changing the task's project clears it.</param>
public sealed record ConversationInfo(
	Guid Id,
	SettingsProfile Profile,
	Guid? ProjectId,
	string Title,
	DateTimeOffset UpdatedAt,
	string? BackendSessionPath = null,
	string? Draft = null,
	bool IsPinned = false,
	bool IsTitleChosen = false,
	bool IsTitleGenerated = false,
	DateTimeOffset CreatedAt = default,
	bool HasChat = true,
	string? Directory = null,
	string? Worktree = null,
	bool IsTaskCheckOff = false,
	int? Position = null );

/// <summary>Stores a conversation message and optional backend-defined rich JSON content.</summary>
public sealed record ConversationMessage(
	Guid Id,
	string Role,
	string Text,
	DateTimeOffset CreatedAt,
	JsonElement? RichContent = null );

/// <summary>Identifies the account-pinned conversation restored or opened in a chat panel.</summary>
/// <param name="ConversationId">The conversation shown.</param>
/// <param name="Profile">The account profile the conversation is pinned to.</param>
/// <param name="ContinueTurn">Whether a module reload stopped the agent mid-turn, so the reopened chat asks it to carry on.</param>
public readonly record struct ConversationPanelInput( Guid ConversationId, SettingsProfile Profile, bool ContinueTurn = false );
