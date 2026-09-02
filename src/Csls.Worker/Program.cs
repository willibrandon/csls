using Csls.Control;
using Csls.Control.Contracts;
using Csls.Core;
using Csls.Protocol;
using Csls.Rpc;
using Csls.Server;
using Csls.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

if (args is ["--msbuild-build-host"])
{
    using Stream input = Console.OpenStandardInput();
    using Stream output = Console.OpenStandardOutput();
    await MSBuildBuildHostServer.RunAsync(input, output).ConfigureAwait(false);
    return 0;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder();
var logFilter = new LanguageServerLogFilter();
builder.Logging.ClearProviders();
builder.Logging.AddFilter((_, level) => logFilter.IsEnabled(level));
builder.Logging.AddConsole(options =>
{
    options.FormatterName = ConsoleFormatterNames.Simple;
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
using var controlLogBuffer = new ControlLogBuffer();
builder.Logging.AddProvider(controlLogBuffer);
builder.Services.AddSingleton(logFilter);
builder.Services.AddSingleton(controlLogBuffer);
builder.Services.AddSingleton<RequestScheduler>();
builder.Services.AddSingleton<WorkspaceLoader, MSBuildWorkspaceLoader>();
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<LspClientConnection>();
builder.Services.AddSingleton<ILspClientConnection>(
    static services => services.GetRequiredService<LspClientConnection>());
builder.Services.AddSingleton<LanguageServer>();
builder.Services.AddSingleton<ControlService>();
builder.Services.AddSingleton<IControlRpcTarget>(
    static services => services.GetRequiredService<ControlService>());
builder.Services.AddSingleton<ControlRpcServer>();
builder.Services.AddSingleton<IHostedService>(
    static services => services.GetRequiredService<ControlRpcServer>());

using IHost host = builder.Build();
IHostApplicationLifetime applicationLifetime =
    host.Services.GetRequiredService<IHostApplicationLifetime>();
await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
try
{
    LanguageServer languageServer = host.Services.GetRequiredService<LanguageServer>();
    LspClientConnection client = host.Services.GetRequiredService<LspClientConnection>();
    using Stream input = Console.OpenStandardInput();
    using Stream output = Console.OpenStandardOutput();
    await RunSessionAsync(
        input,
        output,
        languageServer,
        client,
        applicationLifetime.ApplicationStopping).ConfigureAwait(false);
}
catch (OperationCanceledException) when (
    applicationLifetime.ApplicationStopping.IsCancellationRequested)
{
    // The Generic Host translates SIGINT and SIGTERM into application stopping.
}
finally
{
    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
}
return 0;

static async Task RunSessionAsync(
    Stream input,
    Stream output,
    LanguageServer languageServer,
    LspClientConnection client,
    CancellationToken cancellationToken)
{
    using var sessionSource = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
    Task rpcTask = LspRpcServer.RunAsync(
        input,
        output,
        languageServer,
        client,
        sessionSource.Token);
    Task exitTask = languageServer.WaitForExitAsync(sessionSource.Token);
    await Task.WhenAny(rpcTask, exitTask).ConfigureAwait(false);
    await sessionSource.CancelAsync().ConfigureAwait(false);
    try
    {
        await Task.WhenAll(rpcTask, exitTask).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (sessionSource.IsCancellationRequested)
    {
        return;
    }
}
