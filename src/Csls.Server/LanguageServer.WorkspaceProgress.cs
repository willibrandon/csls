using Csls.Protocol;
using Csls.Workspaces;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using System.Xml;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private async Task LoadWorkspaceWithProgressAsync(CancellationToken cancellationToken)
    {
        if (!_supportsWorkDoneProgress)
        {
            await _workspaceManager.LoadAsync(_rootPaths, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        string progressToken = $"workspace-load-{Guid.NewGuid():N}";
        await _client.CreateWorkDoneProgressAsync(
            new WorkDoneProgressCreateParams
            {
                Token = progressToken
            },
            cancellationToken).ConfigureAwait(false);
        await _client.PublishWorkDoneProgressAsync(
            new WorkDoneProgressParams
            {
                Token = progressToken,
                Value = new WorkDoneProgressBegin
                {
                    Title = "Loading C# workspace",
                    Cancellable = false,
                    Percentage = 0
                }
            }).ConfigureAwait(false);

        var progressChannel =
            Channel.CreateUnbounded<WorkspaceLoadProgress>(
                new UnboundedChannelOptions
                {
                    AllowSynchronousContinuations = false,
                    SingleReader = true,
                    SingleWriter = false
                });
        var progress = new WorkspaceLoadProgressSink(progressChannel.Writer);
        Task<int> forwardingTask = ForwardWorkspaceLoadProgressAsync(
            progressToken,
            progressChannel.Reader);
        Exception? failure = null;
        try
        {
            await _workspaceManager.LoadAsync(
                _rootPaths,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedWorkspaceProgressFailure(exception))
        {
            failure = exception;
            LanguageServerLogger.LogWorkspaceLoadFailure(_logger, exception);
        }
        finally
        {
            progressChannel.Writer.TryComplete();
        }

        int completedProjects = 0;
        try
        {
            completedProjects = await forwardingTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedWorkspaceProgressFailure(exception))
        {
            LanguageServerLogger.LogWorkspaceProgressFailure(_logger, exception);
            failure ??= exception;
        }

        string finalMessage = failure switch
        {
            null => $"Loaded {completedProjects} projects.",
            OperationCanceledException => "Workspace loading was canceled.",
            _ => "Workspace loading did not complete."
        };
        try
        {
            await _client.PublishWorkDoneProgressAsync(
                new WorkDoneProgressParams
                {
                    Token = progressToken,
                    Value = new WorkDoneProgressEnd
                    {
                        Message = finalMessage
                    }
                }).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedWorkspaceProgressFailure(exception))
        {
            LanguageServerLogger.LogWorkspaceProgressFailure(_logger, exception);
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task<int> ForwardWorkspaceLoadProgressAsync(
        string progressToken,
        ChannelReader<WorkspaceLoadProgress> reader)
    {
        int completedProjects = 0;
        await foreach (WorkspaceLoadProgress progress in
            reader.ReadAllAsync().ConfigureAwait(false))
        {
            completedProjects = progress.CompletedProjects;
            await _client.PublishWorkDoneProgressAsync(
                new WorkDoneProgressParams
                {
                    Token = progressToken,
                    Value = new WorkDoneProgressReport
                    {
                        Cancellable = false,
                        Message = $"{progress.ProjectName} " +
                            $"({progress.CompletedProjects}/{progress.TotalProjects})",
                        Percentage = progress.Percentage
                    }
                }).ConfigureAwait(false);
        }

        return completedProjects;
    }

    private static bool IsExpectedWorkspaceProgressFailure(Exception exception) =>
        exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            NotSupportedException or
            OperationCanceledException or
            UnauthorizedAccessException or
            XmlException;

}
