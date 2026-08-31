import { access } from "node:fs/promises";
import { isAbsolute, join } from "node:path";
import * as vscode from "vscode";
import {
  type LanguageClientOptions,
  RevealOutputChannelOn,
  type ServerOptions,
  State,
} from "vscode-languageclient/node";
import { registerCSharpVirtualDocumentProvider } from "./csharpVirtualDocumentProvider.js";
import { DesktopLanguageClient } from "./desktopLanguageClient.js";
import { DebuggerProvider } from "./debuggerProvider.js";
import { LanguageServerLogOutputChannel } from "./languageServerLogOutputChannel.js";
import { WorkspaceExperience } from "./workspaceExperience.js";

const extensionId = "willibrandon.csls";
const runtimeVersion = "10.0";
const conflictingExtensionIds = ["ms-dotnettools.csharp", "ms-dotnettools.csdevkit"];

let client: DesktopLanguageClient | undefined;
let outputChannel: vscode.LogOutputChannel | undefined;
let workspaceExperience: WorkspaceExperience | undefined;

export interface CslsExtensionApi {
  readonly host: "desktop" | "remote";
  readonly projects: () => readonly {
    readonly name: string;
    readonly path: string;
  }[];
  readonly runtimePath: string;
  readonly sdkPath: string;
  readonly serverPath: string;
  readonly state: State;
  readonly testErrors: () => readonly string[];
  readonly tests: () => readonly string[];
}

interface DotnetAcquireResult {
  readonly dotnetPath: string;
}

interface DotnetAcquireContext {
  readonly architecture: string;
  readonly mode: "runtime" | "sdk";
  readonly requestingExtensionId: string;
  readonly version: string;
}

interface DotnetFindPathContext {
  readonly acquireContext: DotnetAcquireContext;
  readonly versionSpecRequirement: "greater_than_or_equal";
}

export async function activate(context: vscode.ExtensionContext): Promise<CslsExtensionApi> {
  const conflicts = conflictingExtensionIds.filter(
    (identifier) => vscode.extensions.getExtension(identifier) !== undefined,
  );
  if (conflicts.length > 0) {
    const names = conflicts.join(", ");
    void vscode.window.showErrorMessage(
      `Disable ${names} before starting csls. Running more than one C# language server produces duplicate and conflicting results.`,
    );
    throw new Error(`Conflicting C# extensions are enabled: ${names}`);
  }

  outputChannel = vscode.window.createOutputChannel("csls", { log: true });
  context.subscriptions.push(outputChannel);
  await vscode.commands.executeCommand("setContext", "csls.workspaceExperience", true);
  await vscode.commands.executeCommand("setContext", "csls.desktopWorkspaceExperience", true);
  const runtimePath = await acquireRuntime();
  const sdkPath = await resolveSdk();
  const serverPath = await resolveServerPath(context);
  const watchers = createFileWatchers(context);
  client = createLanguageClient(serverPath, runtimePath, watchers);
  const debuggerProvider = new DebuggerProvider(
    context,
    serverPath,
    runtimePath,
    outputChannel,
  );
  workspaceExperience = new WorkspaceExperience(sdkPath, outputChannel);
  registerCSharpVirtualDocumentProvider(context, () => client);
  context.subscriptions.push(
    vscode.commands.registerCommand("csls.restartServer", restartServer),
    vscode.commands.registerCommand("csls.showOutput", () => outputChannel?.show()),
    client,
    debuggerProvider,
    workspaceExperience,
  );
  await client.start();
  await workspaceExperience.attach(client);
  outputChannel.info(`Started ${serverPath} with .NET ${runtimeVersion} from ${runtimePath}.`);
  return {
    host: vscode.env.remoteName === undefined ? "desktop" : "remote",
    projects: () => workspaceExperience?.getProjects() ?? [],
    runtimePath,
    sdkPath,
    serverPath,
    state: client.state,
    testErrors: () => workspaceExperience?.getTestErrors() ?? [],
    tests: () => workspaceExperience?.getTestNames() ?? [],
  };
}

export async function deactivate(): Promise<void> {
  workspaceExperience?.dispose();
  workspaceExperience = undefined;
  if (client !== undefined) {
    await client.stop();
    client = undefined;
  }

  outputChannel = undefined;
}

async function acquireRuntime(): Promise<string> {
  const acquireContext: DotnetAcquireContext = {
    architecture: getDotnetArchitecture(),
    mode: "runtime",
    requestingExtensionId: extensionId,
    version: runtimeVersion,
  };
  const result = await vscode.commands.executeCommand<DotnetAcquireResult>(
    "dotnet.acquire",
    acquireContext,
  );
  if (result?.dotnetPath === undefined || result.dotnetPath.length === 0) {
    throw new Error("The .NET Install Tool did not provide a .NET 10 runtime.");
  }

  await access(result.dotnetPath);
  return result.dotnetPath;
}

async function resolveSdk(): Promise<string> {
  const acquireContext: DotnetAcquireContext = {
    architecture: getDotnetArchitecture(),
    mode: "sdk",
    requestingExtensionId: extensionId,
    version: runtimeVersion,
  };
  const findContext: DotnetFindPathContext = {
    acquireContext,
    versionSpecRequirement: "greater_than_or_equal",
  };
  const installed = await vscode.commands.executeCommand<DotnetAcquireResult | undefined>(
    "dotnet.findPath",
    findContext,
  );
  const result = installed ?? await vscode.commands.executeCommand<DotnetAcquireResult | undefined>(
    "dotnet.acquire",
    acquireContext,
  );
  if (result?.dotnetPath === undefined || result.dotnetPath.length === 0) {
    throw new Error("The .NET Install Tool did not provide a .NET 10 SDK.");
  }

  await access(result.dotnetPath);
  return result.dotnetPath;
}

function getDotnetArchitecture(): string {
  return process.arch === "ia32" ? "x86" : process.arch;
}

async function resolveServerPath(context: vscode.ExtensionContext): Promise<string> {
  const configuredPath = vscode.workspace
    .getConfiguration("csls.server")
    .get<string>("path", "")
    .trim();
  const serverPath = configuredPath.length > 0
    ? configuredPath
    : join(context.extensionPath, "server", process.platform === "win32" ? "csls.exe" : "csls");
  if (!isAbsolute(serverPath)) {
    throw new Error("csls.server.path must be absolute when it is configured.");
  }

  await access(serverPath);
  return serverPath;
}

function createFileWatchers(context: vscode.ExtensionContext): vscode.FileSystemWatcher[] {
  const patterns = [
    "**/*.{cs,csx,razor,cshtml}",
    "**/*.{sln,slnx,csproj,props,targets}",
    "**/{global.json,NuGet.Config,nuget.config,Directory.Build.props,Directory.Build.targets,Directory.Packages.props}",
  ];
  return patterns.map((pattern) => {
    const watcher = vscode.workspace.createFileSystemWatcher(pattern);
    context.subscriptions.push(watcher);
    return watcher;
  });
}

function createLanguageClient(
  serverPath: string,
  runtimePath: string,
  watchers: readonly vscode.FileSystemWatcher[],
): DesktopLanguageClient {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (outputChannel === undefined) {
    throw new Error("The csls output channel was not initialized.");
  }

  const environment = {
    ...process.env,
    CSLS_RUNTIME_HOST_PATH: runtimePath,
  };
  const managedLauncher = serverPath.endsWith(".dll");
  const serverOptions: ServerOptions = {
    command: managedLauncher ? runtimePath : serverPath,
    args: managedLauncher ? [serverPath, "lsp"] : ["lsp"],
    options: {
      env: environment,
      ...(workspaceFolder === undefined ? {} : { cwd: workspaceFolder.uri.fsPath }),
    },
  };
  const clientOptions: LanguageClientOptions = {
    diagnosticPullOptions: {
      onChange: true,
      onFocus: true,
      onSave: true,
      onTabs: true,
    },
    documentSelector: [
      { language: "csharp", scheme: "file" },
      { language: "razor", scheme: "file" },
    ],
    outputChannel: new LanguageServerLogOutputChannel(outputChannel),
    revealOutputChannelOn: RevealOutputChannelOn.Error,
    middleware: {
      provideWorkspaceDiagnostics: (
        previousResultIds,
        token,
        resultReporter,
        next,
      ) => shouldRunFullSolutionDiagnostics()
        ? next(previousResultIds, token, resultReporter)
        : { items: [] },
    },
    synchronize: {
      configurationSection: ["csls", "csharp", "dotnet"],
      fileEvents: [...watchers],
    },
    ...(workspaceFolder === undefined ? {} : { workspaceFolder }),
  };
  return new DesktopLanguageClient(serverOptions, clientOptions);
}

function shouldRunFullSolutionDiagnostics(): boolean {
  const configuration = vscode.workspace.getConfiguration("dotnet.backgroundAnalysis");
  return configuration.get<string>("analyzerDiagnosticsScope", "openFiles") ===
      "fullSolution" ||
    configuration.get<string>("compilerDiagnosticsScope", "openFiles") === "fullSolution";
}

async function restartServer(): Promise<void> {
  if (client === undefined) {
    return;
  }

  outputChannel?.info("Restarting the language server.");
  await client.restart();
  await workspaceExperience?.attach(client);
}
