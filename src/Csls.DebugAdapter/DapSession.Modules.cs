using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Reports loaded modules and their symbol and optimization state.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteModulesAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Running and not DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int startModule = GetOptionalNonNegativeInteger(
                request.Arguments,
                "startModule",
                "modules");
            int moduleCount = GetOptionalNonNegativeInteger(
                request.Arguments,
                "moduleCount",
                "modules");
            DebugModulePage page = await _engineSession
                .GetModulesAsync(startModule, moduleCount, cancellationToken)
                .ConfigureAwait(false);
            await WriteModulePageAsync(request, page, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private ValueTask WriteModulePageAsync(
        Request request,
        DebugModulePage page,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("modules");
                foreach (DebugModuleInfo module in page.Modules)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", module.Id);
                    writer.WriteString("name", module.Name);
                    if (module.Path is not null)
                    {
                        writer.WriteString("path", module.Path);
                    }

                    if (module.IsOptimized is bool isOptimized)
                    {
                        writer.WriteBoolean("isOptimized", isOptimized);
                    }

                    writer.WriteString("symbolStatus", GetModuleStatus(module));
                    if (module.SymbolPath is not null)
                    {
                        writer.WriteString("symbolFilePath", module.SymbolPath);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("totalModules", page.TotalModules);
                writer.WriteEndObject();
            },
            cancellationToken);

    private static string GetModuleStatus(DebugModuleInfo module)
    {
        string symbolStatus = module.SymbolKind switch
        {
            DebugModuleSymbolKind.PortablePdb => "Symbols loaded.",
            DebugModuleSymbolKind.EmbeddedPortablePdb => "Embedded Portable PDB loaded.",
            _ => "Symbols not found."
        };
        return module.OptimizationDiagnostic is null
            ? symbolStatus
            : $"{symbolStatus} {module.OptimizationDiagnostic}";
    }
}
