using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies attached-session resources, prompts, and workspace maintenance through MCP.
/// </summary>
public sealed partial class McpLanguageServerTests
{
    private async Task AssertResourcesPromptsAndMaintenanceAsync(
        McpClient client,
        int processId,
        string projectPath,
        string documentPath)
    {
        IList<McpClientResource> resources = await client
            .ListResourcesAsync(cancellationToken: TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.IsEmpty(resources);
        ReadResourceResult resourceResult = await client.ReadResourceAsync(
            "csls://session/{?workspace,session,socket}",
            new Dictionary<string, object?> { ["session"] = processId },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        TextResourceContents sessionResource = resourceResult.Contents
            .OfType<TextResourceContents>()
            .Single();
        ControlSessionInfo resourceSession = JsonSerializer.Deserialize(
            sessionResource.Text,
            ControlJsonSerializerContext.Default.ControlSessionInfo)
            ?? throw new InvalidDataException("MCP returned no session resource value.");
        Assert.AreEqual(processId, resourceSession.ProcessId);

        ReadResourceResult workspaceResourceResult = await client.ReadResourceAsync(
            "csls://workspace/{?workspace,session,socket}",
            new Dictionary<string, object?> { ["session"] = processId },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        TextResourceContents workspaceResource = workspaceResourceResult.Contents
            .OfType<TextResourceContents>()
            .Single();
        ControlDashboardSnapshot resourceWorkspace = JsonSerializer.Deserialize(
            workspaceResource.Text,
            ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
            ?? throw new InvalidDataException(
                "MCP returned no workspace resource value.");
        Assert.AreEqual(processId, resourceWorkspace.Session.ProcessId);

        IList<McpClientResourceTemplate> resourceTemplates = await client
            .ListResourceTemplatesAsync(cancellationToken: TestContext.CancellationToken)
            .ConfigureAwait(false);
        IEnumerable<string> resourceTemplateUris = resourceTemplates.Select(
            static resource => resource.UriTemplate);
        Assert.Contains(
            "csls://session/{?workspace,session,socket}",
            resourceTemplateUris);
        Assert.Contains(
            "csls://workspace/{?workspace,session,socket}",
            resourceTemplateUris);
        Assert.Contains(
            "csls://project/{?workspace,session,socket,path}",
            resourceTemplateUris);
        Assert.Contains(
            "csls://document/{?workspace,session,socket,path}",
            resourceTemplateUris);
        Assert.Contains(
            "csls://diagnostic/{?workspace,session,socket,path}",
            resourceTemplateUris);

        ReadResourceResult projectResourceResult = await client.ReadResourceAsync(
            "csls://project/{?workspace,session,socket,path}",
            new Dictionary<string, object?>
            {
                ["session"] = processId,
                ["path"] = projectPath
            },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        ControlProjectInfo resourceProject = JsonSerializer.Deserialize(
            projectResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
            ControlJsonSerializerContext.Default.ControlProjectInfo)
            ?? throw new InvalidDataException("MCP returned no project resource value.");
        Assert.AreEqual(projectPath, resourceProject.FilePath);
        Assert.AreEqual("Fixture", resourceProject.Name);

        ReadResourceResult documentResourceResult = await client.ReadResourceAsync(
            "csls://document/{?workspace,session,socket,path}",
            new Dictionary<string, object?>
            {
                ["session"] = processId,
                ["path"] = documentPath
            },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        ControlDocumentInfo resourceDocument = JsonSerializer.Deserialize(
            documentResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
            ControlJsonSerializerContext.Default.ControlDocumentInfo)
            ?? throw new InvalidDataException("MCP returned no document resource value.");
        Assert.AreEqual(documentPath, resourceDocument.FilePath);
        Assert.IsTrue(resourceDocument.IsOpen);

        ReadResourceResult diagnosticResourceResult = await client.ReadResourceAsync(
            "csls://diagnostic/{?workspace,session,socket,path}",
            new Dictionary<string, object?>
            {
                ["session"] = processId,
                ["path"] = documentPath
            },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        DocumentDiagnosticReport resourceDiagnostics = JsonSerializer.Deserialize(
            diagnosticResourceResult.Contents.OfType<TextResourceContents>().Single().Text,
            ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
            ?? throw new InvalidDataException(
                "MCP returned no diagnostic resource value.");
        Assert.Contains(
            "CS0103",
            resourceDiagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

        IList<McpClientPrompt> prompts = await client
            .ListPromptsAsync(cancellationToken: TestContext.CancellationToken)
            .ConfigureAwait(false);
        IEnumerable<string> promptNames = prompts.Select(static prompt => prompt.Name);
        Assert.Contains("diagnose_csharp", promptNames);
        Assert.Contains("explain_symbol", promptNames);
        Assert.Contains("review_csharp", promptNames);
        Assert.Contains("refactor_csharp", promptNames);
        Assert.Contains("troubleshoot_csls", promptNames);
        GetPromptResult promptResult = await client.GetPromptAsync(
            "diagnose_csharp",
            new Dictionary<string, object?> { ["scope"] = documentPath },
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsNotEmpty(promptResult.Messages);

        ControlWorkspaceOperationResult clearResult =
            await CallWorkspaceOperationAsync(
                client,
                processId,
                "clear_caches",
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("clear-cache", clearResult.Operation);
        Assert.AreEqual(
            clearResult.PreviousGeneration,
            clearResult.CurrentGeneration);
        Assert.IsGreaterThan(0, clearResult.ClearedCacheEntryCount);

        ControlWorkspaceOperationResult reloadResult =
            await CallWorkspaceOperationAsync(
                client,
                processId,
                "reload_workspace",
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("reload", reloadResult.Operation);
        Assert.AreEqual(
            reloadResult.PreviousGeneration + 1,
            reloadResult.CurrentGeneration);

        ControlWorkspaceOperationResult restartResult =
            await CallWorkspaceOperationAsync(
                client,
                processId,
                "restart_build_hosts",
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("restart-build-host", restartResult.Operation);
        Assert.AreEqual(
            restartResult.PreviousGeneration + 1,
            restartResult.CurrentGeneration);
        Assert.IsGreaterThan(0, restartResult.RestartedBuildHostCount);

        ControlWorkspaceOperationResult restoreResult =
            await CallWorkspaceOperationAsync(
                client,
                processId,
                "restore_workspace",
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("restore", restoreResult.Operation);
        Assert.AreEqual(
            restoreResult.PreviousGeneration + 1,
            restoreResult.CurrentGeneration);
        Assert.AreEqual(1, restoreResult.RestoredEntryPointCount);
    }
}
