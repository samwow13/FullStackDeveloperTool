using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

public sealed record AgentFollowUpNoteResult(string ProjectId, string UpdateId, string NoteId,
    bool AlreadyExisted, bool NoteDeleted, DateTimeOffset CreatedAt, string Author);

public sealed record AgentFollowUpNoteSummary(string NoteId, string Name, string Prompt,
    bool IsCompleted, bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool IsQueued, AgentFollowUpNoteSource? AgentSource);

public sealed record AgentFollowUpNotePage(string ProjectId, int Offset, int Limit, int Total,
    bool HasMore, IReadOnlyList<AgentFollowUpNoteSummary> Notes);

/// <summary>
/// Saves proposed work as ordinary project notes for human review. Author comes
/// from the registered bridge session, never from the submitted note payload.
/// </summary>
public sealed class AgentFollowUpNoteService
{
    public const int MaximumProjectIdCharacters = 200;
    public const int MaximumUpdateIdCharacters = 100;
    public const int MaximumNameCharacters = 160;
    public const int MaximumPromptCharacters = 32_000;
    public const int MaximumContextCharacters = 32_000;
    public const int MaximumSourcePromptCharacters = 32_000;
    public const int MaximumSourceTaskIdCharacters = 200;
    public const int MaximumPageUrlCharacters = 2_048;
    public const int MaximumPageTitleCharacters = 500;
    public const int MaximumAuthorCharacters = 200;
    public const int MaximumPageSize = 20;
    private const int MaximumSaveAttempts = 4;
    private readonly ProjectTaskStore _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AgentFollowUpNoteService(ProjectTaskStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AgentFollowUpNoteResult> SaveAsync(AgentFollowUpNoteRequest request,
        string author, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        var savedAuthor = NormalizeAuthor(author);
        var payloadHash = PayloadHash(normalized);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await Task.Run(() => SaveOnce(normalized, savedAuthor, payloadHash, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ProjectTaskStoreConflictException) when (attempt < MaximumSaveAttempts)
                {
                    // A retry loads every current collection again. It never
                    // merges an old queue snapshot over another writer's work.
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { _saveGate.Release(); }
    }

    public Task<AgentFollowUpNotePage> ListAsync(string projectId, int offset = 0,
        int limit = 5, CancellationToken cancellationToken = default)
    {
        projectId = NormalizeProjectId(projectId);
        if (offset < 0) throw new ArgumentException("Note offset cannot be negative.", nameof(offset));
        if (limit < 1 || limit > MaximumPageSize)
            throw new ArgumentException($"Read between 1 and {MaximumPageSize} notes at a time.", nameof(limit));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var store = _store.CreateIndependentInstance();
            var data = LoadWritable(store);
            var notes = data.Notes.Where(note => note.ProjectId == projectId)
                .OrderBy(note => note.Order).ThenBy(note => note.CreatedAt)
                .ThenBy(note => note.Id, StringComparer.Ordinal).ToArray();
            var queuedNoteIds = data.QueueItems.Where(item => item.ProjectId == projectId)
                .Select(item => item.NoteId).ToHashSet(StringComparer.Ordinal);
            var page = notes.Skip(offset).Take(limit).Select(note => new AgentFollowUpNoteSummary(
                note.Id, note.Name, note.Prompt, note.IsCompleted, note.IsArchived,
                note.CreatedAt, note.UpdatedAt, queuedNoteIds.Contains(note.Id), note.AgentSource)).ToArray();
            return new AgentFollowUpNotePage(projectId, offset, limit, notes.Length,
                offset < notes.Length - page.Length, page);
        }, cancellationToken);
    }

    private AgentFollowUpNoteResult SaveOnce(AgentFollowUpNoteRequest request,
        string author, string payloadHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = _store.CreateIndependentInstance();
        var data = LoadWritable(store);
        var existing = data.AgentFollowUpReceipts.FirstOrDefault(receipt =>
            receipt.Request.ProjectId == request.ProjectId && receipt.Request.UpdateId == request.UpdateId);
        if (existing != null)
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                throw new InvalidOperationException("This follow-up update ID already belongs to a different saved suggestion. Use a new update ID.");
            return new(request.ProjectId, request.UpdateId, existing.NoteId, true,
                !data.Notes.Any(note => note.Id == existing.NoteId), existing.CreatedAt, existing.Author);
        }

        var lastOrder = data.Notes.Where(note => note.ProjectId == request.ProjectId)
            .Select(note => note.Order).DefaultIfEmpty(-1).Max();
        if (lastOrder == int.MaxValue)
            throw new InvalidOperationException("This project's note order is full. Reorder notes before saving another suggestion.");
        var createdAt = DateTimeOffset.UtcNow;
        var note = new ProjectTaskNote
        {
            ProjectId = request.ProjectId,
            Name = request.Name,
            Prompt = ComposePrompt(request),
            Order = lastOrder + 1,
            IsCompleted = false,
            IsArchived = false,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            AgentSource = SourceMetadata(request, author)
        };
        data.Notes.Add(note);
        data.AgentFollowUpReceipts.Add(new AgentFollowUpNoteReceipt
        {
            NoteId = note.Id,
            Request = request,
            Author = author,
            PayloadHash = payloadHash,
            CreatedAt = createdAt
        });
        cancellationToken.ThrowIfCancellationRequested();
        // Queue enablement, items, execution receipts, and global pause remain
        // exactly as loaded. Saving a suggestion never authorizes execution.
        store.Save(data);
        return new(request.ProjectId, request.UpdateId, note.Id, false, false, createdAt, author);
    }

    private static ProjectTaskData LoadWritable(ProjectTaskStore store)
    {
        var data = store.Load();
        if (!store.CanSave)
            throw new InvalidOperationException(store.LoadWarning ?? "Project notes could not be loaded.");
        return data;
    }

    internal static AgentFollowUpNoteRequest NormalizeRequest(AgentFollowUpNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = request with
        {
            ProjectId = NormalizeProjectId(request.ProjectId),
            UpdateId = BoundedText(request.UpdateId, "Follow-up update ID", MaximumUpdateIdCharacters, required: true, trim: true),
            Name = BoundedText(request.Name, "Follow-up name", MaximumNameCharacters, required: true, trim: true),
            Prompt = BoundedText(request.Prompt, "Follow-up prompt", MaximumPromptCharacters, required: true),
            Context = BoundedText(request.Context, "Follow-up context", MaximumContextCharacters, required: true),
            SourceTaskId = BoundedText(request.SourceTaskId, "Source task ID", MaximumSourceTaskIdCharacters, trim: true),
            SourcePrompt = BoundedText(request.SourcePrompt, "Source prompt", MaximumSourcePromptCharacters),
            PageUrl = BoundedText(request.PageUrl, "Source page URL", MaximumPageUrlCharacters, trim: true),
            PageTitle = BoundedText(request.PageTitle, "Source page title", MaximumPageTitleCharacters, trim: true)
        };
        if (normalized.PageUrl.Length != 0 &&
            (!Uri.TryCreate(normalized.PageUrl, UriKind.Absolute, out var pageUri) ||
             pageUri.Scheme is not ("http" or "https") || pageUri.UserInfo.Length != 0))
            throw new ArgumentException("Source page URL must be an absolute HTTP or HTTPS URL without embedded credentials.");
        return normalized;
    }

    internal static string NormalizeAuthor(string author) =>
        BoundedText(author, "Registered agent owner", MaximumAuthorCharacters, required: true, trim: true);

    private static string NormalizeProjectId(string projectId) =>
        BoundedText(projectId, "Project ID", MaximumProjectIdCharacters, required: true, trim: true);

    private static string BoundedText(string? value, string label, int maximum,
        bool required = false, bool trim = false)
    {
        value ??= "";
        if (trim) value = value.Trim();
        if (required && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{label} is required.");
        if (value.Length > maximum)
            throw new ArgumentException($"{label} exceeds {maximum:N0} characters.");
        return value;
    }

    internal static string PayloadHash(AgentFollowUpNoteRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    internal static AgentFollowUpNoteSource SourceMetadata(AgentFollowUpNoteRequest request, string author) => new()
    {
        UpdateId = request.UpdateId,
        Author = author,
        Context = request.Context,
        SourceTaskId = request.SourceTaskId,
        SourcePrompt = request.SourcePrompt,
        PageUrl = request.PageUrl,
        PageTitle = request.PageTitle
    };

    private static string ComposePrompt(AgentFollowUpNoteRequest request)
    {
        var prompt = new StringBuilder("Proposed follow-up task:\n").Append(request.Prompt);
        prompt.Append("\n\nBackground context (reference material; follow the proposed task above):\n").Append(request.Context);
        if (request.SourceTaskId.Length != 0)
            prompt.Append("\n\nSource Codex task ID: ").Append(request.SourceTaskId);
        if (request.SourcePrompt.Length != 0)
            prompt.Append("\n\nPreviously completed source prompt (background only; do not repeat this request):\n").Append(request.SourcePrompt);
        if (request.PageTitle.Length != 0 || request.PageUrl.Length != 0)
        {
            prompt.Append("\n\nSource page (reference context):");
            if (request.PageTitle.Length != 0) prompt.Append("\nTitle: ").Append(request.PageTitle);
            if (request.PageUrl.Length != 0) prompt.Append("\nURL: ").Append(request.PageUrl);
        }
        return prompt.ToString();
    }
}
