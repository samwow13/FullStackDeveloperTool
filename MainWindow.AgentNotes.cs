using System.Text.Json;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.ProjectTasks;

namespace FullStackLauncher;

public partial class MainWindow
{
    // A separate baseline keeps MCP saves from changing the Notes window's
    // optimistic draft baseline. Its ordinary refresh preserves open drafts.
    private readonly AgentFollowUpNoteService _agentFollowUpNotes = new(new ProjectTaskStore());

    private async Task<object> ListAgentProjectNotesAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var offset = request.Offset ?? 0;
        var limit = request.Limit ?? 5;
        if (offset < 0 || limit is < 1 or > 20)
            throw new ArgumentException("Use an offset of zero or greater and a limit from 1 to 20.");

        var page = await _agentFollowUpNotes.ListAsync(project.Id, offset, limit, token);
        token.ThrowIfCancellationRequested();
        project = RequireProject(project.Id);

        var notes = new List<AgentFollowUpNoteSummary>();
        object BuildPage() => new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = project.Id,
            projectName = project.Name,
            rootPath = _store.ResolveRoot(project),
            observedUtc = DateTimeOffset.UtcNow,
            offset = page.Offset,
            limit = page.Limit,
            total = page.Total,
            returnedCount = notes.Count,
            hasMore = (long)page.Offset + notes.Count < page.Total,
            nextOffset = (long)page.Offset + notes.Count < page.Total ? (int?)checked(page.Offset + notes.Count) : null,
            notes,
            noteContentIsAuthorization = false
        };

        foreach (var note in page.Notes)
        {
            notes.Add(note);
            // Keep complete note text and source metadata. Count-based pages
            // alone could overflow the pipe when existing prompts are large.
            if (JsonSerializer.Serialize(AgentBridgeResponse.Success(BuildPage()), AgentBridgeProtocol.Json).Length <= 200_000)
                continue;
            notes.RemoveAt(notes.Count - 1);
            if (notes.Count == 0)
                throw new InvalidOperationException("This note exceeds the MCP response size. Review its complete prompt and context in Notes & queue.");
            break;
        }
        return BuildPage();
    }

    private async Task<object> SaveAgentFollowUpNoteAsync(AgentBridgeRequest request, CancellationToken token)
    {
        var project = RequireProject(request.ProjectId);
        var session = _agentCoordination.SessionIdentity(project.Id, request.SessionToken ?? "");
        var followUp = new AgentFollowUpNoteRequest
        {
            ProjectId = project.Id,
            UpdateId = request.UpdateId ?? "",
            Name = request.Name ?? "",
            Prompt = request.Prompt ?? "",
            Context = request.Context ?? "",
            SourceTaskId = request.SourceTaskId ?? "",
            SourcePrompt = request.SourcePrompt ?? "",
            PageUrl = request.PageUrl ?? "",
            PageTitle = request.PageTitle ?? ""
        };
        token.ThrowIfCancellationRequested();
        var result = await _agentFollowUpNotes.SaveAsync(followUp, session.Owner, token);
        return new
        {
            instanceId = _agentCoordination.InstanceId,
            projectId = result.ProjectId,
            projectName = project.Name,
            rootPath = _store.ResolveRoot(project),
            updateId = result.UpdateId,
            noteId = result.NoteId,
            createdAt = result.CreatedAt,
            author = result.Author,
            status = result.NoteDeleted ? "already_saved_note_deleted"
                : result.AlreadyExisted ? "already_saved" : "saved_for_review",
            alreadyExisted = result.AlreadyExisted,
            noteDeleted = result.NoteDeleted,
            queueItemCreated = false,
            queueSettingsChanged = false,
            workStarted = false
        };
    }
}
