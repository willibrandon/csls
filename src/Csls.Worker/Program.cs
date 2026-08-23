using Csls.Control;
using Csls.Control.Contracts;
using Csls.Core;
using Csls.Rpc;
using Csls.Server;
using Csls.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

HostApplicationBuilder builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.FormatterName = ConsoleFormatterNames.Simple;
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton<RequestScheduler>();
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<LanguageServer>();
builder.Services.AddSingleton<ControlService>();
builder.Services.AddSingleton<IControlRpcTarget>(
    static services => services.GetRequiredService<ControlService>());
builder.Services.AddSingleton<ControlRpcServer>();
builder.Services.AddSingleton<IHostedService>(
    static services => services.GetRequiredService<ControlRpcServer>());

using var shutdownSource = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdownSource.Cancel();
};
Console.CancelKeyPress += cancelHandler;

try
{
    using IHost host = builder.Build();
    await host.StartAsync(shutdownSource.Token).ConfigureAwait(false);
    LanguageServer languageServer = host.Services.GetRequiredService<LanguageServer>();
    using Stream input = Console.OpenStandardInput();
    using Stream output = Console.OpenStandardOutput();
    await RunSessionAsync(
        input,
        output,
        languageServer,
        shutdownSource.Token).ConfigureAwait(false);

    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
    return 0;
}
catch (OperationCanceledException) when (shutdownSource.IsCancellationRequested)
{
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task RunSessionAsync(
    Stream input,
    Stream output,
    LanguageServer languageServer,
    CancellationToken cancellationToken)
{
    using var sessionSource = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
    using CancellationTokenRegistration exitRegistration = languageServer.ExitToken.Register(
        static state => ((CancellationTokenSource)state!).Cancel(),
        sessionSource);
    Task rpcTask = LspRpcServer.RunAsync(
        input,
        output,
        languageServer,
        sessionSource.Token);
    try
    {
        ValueTask completion = new(rpcTask);
        await completion.ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (sessionSource.IsCancellationRequested)
    {
        return;
    }
}
