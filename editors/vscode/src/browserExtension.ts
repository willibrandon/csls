import * as vscode from "vscode";
import {
  DocumentDiagnosticRequest,
  type LanguageClientOptions,
  RevealOutputChannelOn,
  State,
} from "vscode-languageclient/browser";
import { BrowserLanguageClient } from "./browserLanguageClient.js";
import { BrowserWorkspaceMapping } from "./browserWorkspaceMapping.js";
import { BrowserWorkspaceExperience } from "./browserWorkspaceExperience.js";
import { registerCSharpVirtualDocumentProvider } from "./csharpVirtualDocumentProvider.js";
import { registerReferenceCodeLensCommands } from "./referenceCodeLensCommands.js";

const conflictingExtensionIds = ["ms-dotnettools.csharp", "ms-dotnettools.csdevkit"];
const synchronizationInclude = "**/*.{cs,csx,razor,cshtml,sln,slnx,csproj,props,targets,editorconfig,globalconfig,json,config}";
const synchronizationExclude = "**/{.git,.vs,artifacts,bin,node_modules,obj}/**";
const maximumSynchronizedFiles = 20_000;
const maximumSynchronizedBytes = 64 * 1024 * 1024;
const startupTimeoutMilliseconds = 120_000;
const fileSynchronizationTimeoutMilliseconds = 30_000;
const excludedDirectoryNames = new Set([
  ".git",
  ".vs",
  "artifacts",
  "bin",
  "node_modules",
  "obj",
]);
const synchronizedFileExtensions = new Set([
  ".config",
  ".cs",
  ".cshtml",
  ".csproj",
  ".csx",
  ".editorconfig",
  ".globalconfig",
  ".json",
  ".props",
  ".razor",
  ".sln",
  ".slnx",
  ".targets",
]);

let client: BrowserLanguageClient | undefined;
let worker: Worker | undefined;
let languageServerPort: MessagePort | undefined;
let workerEvents: vscode.Disposable | undefined;
let outputChannel: vscode.LogOutputChannel | undefined;
let extensionContext: vscode.ExtensionContext | undefined;
let workspaceExperience: BrowserWorkspaceExperience | undefined;
let workspaceMapping: BrowserWorkspaceMapping | undefined;
let nextControlRequestId = 1;

export interface CslsBrowserExtensionApi {
  readonly host: "browser";
  readonly projects: () => readonly {
    readonly name: string;
    readonly path: string;
  }[];
  readonly state: State;
}

export async function activate(
  context: vscode.ExtensionContext,
): Promise<CslsBrowserExtensionApi> {
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

  extensionContext = context;
  await vscode.commands.executeCommand("setContext", "csls.workspaceExperience", true);
  await vscode.commands.executeCommand("setContext", "csls.desktopWorkspaceExperience", false);
  outputChannel = vscode.window.createOutputChannel("csls", { log: true });
  context.subscriptions.push(
    outputChannel,
    vscode.commands.registerCommand("csls.restartServer", restartServer),
    vscode.commands.registerCommand("csls.showOutput", () => outputChannel?.show()),
  );
  workspaceMapping = new BrowserWorkspaceMapping(vscode.workspace.workspaceFolders ?? []);
  registerReferenceCodeLensCommands(context, workspaceMapping.toCodeUri);
  workspaceExperience = new BrowserWorkspaceExperience(outputChannel, workspaceMapping);
  context.subscriptions.push(workspaceExperience);
  registerCSharpVirtualDocumentProvider(context, () => client);
  await startServer(context);
  return {
    host: "browser",
    projects: () => workspaceExperience?.getProjects() ?? [],
    state: client?.state ?? State.Stopped,
  };
}

export async function deactivate(): Promise<void> {
  workspaceExperience?.dispose();
  workspaceExperience = undefined;
  workspaceMapping = undefined;
  await stopServer();
  extensionContext = undefined;
  outputChannel = undefined;
}

async function startServer(context: vscode.ExtensionContext): Promise<void> {
  const mapping = workspaceMapping ?? new BrowserWorkspaceMapping(
    vscode.workspace.workspaceFolders ?? [],
  );
  const serverUri = vscode.Uri.joinPath(
    context.extensionUri,
    "dist",
    "browserServer",
    "cslsBrowserWorker.js",
  );
  const nextWorker = new Worker(serverUri.toString(), { name: "csls" });
  const nextWorkerEvents = observeWorker(nextWorker);
  const channel = new MessageChannel();
  const dotnetUri = vscode.Uri.joinPath(
    context.extensionUri,
    "dist",
    "browserServer",
    "_framework",
    "dotnet.js",
  );
  try {
    await synchronizeWorkspace(nextWorker, channel.port2, dotnetUri, mapping);
    const nextClient = new BrowserLanguageClient(
      channel.port1,
      createClientOptions(nextWorker, mapping),
      mapping,
    );
    worker = nextWorker;
    workerEvents = nextWorkerEvents;
    languageServerPort = channel.port1;
    client = nextClient;
    context.subscriptions.push(nextClient);
    await nextClient.start();
    await workspaceExperience?.attach(nextClient);
    nextClient.getFeature(DocumentDiagnosticRequest.method).refresh();
    outputChannel?.info("Started csls in the browser WebAssembly worker.");
  } catch (error) {
    channel.port1.close();
    nextWorkerEvents.dispose();
    nextWorker.terminate();
    throw error;
  }
}

async function stopServer(): Promise<void> {
  const activeClient = client;
  const activeWorker = worker;
  const activePort = languageServerPort;
  const activeWorkerEvents = workerEvents;
  client = undefined;
  worker = undefined;
  languageServerPort = undefined;
  workerEvents = undefined;
  try {
    if (activeClient !== undefined) {
      await activeClient.stop();
    }
  } finally {
    activePort?.close();
    activeWorkerEvents?.dispose();
    activeWorker?.terminate();
  }
}

function observeWorker(targetWorker: Worker): vscode.Disposable {
  const messageListener = (event: MessageEvent<unknown>) => {
    if (isWorkerStatusMessage(event.data)) {
      outputChannel?.debug(`Browser worker: ${event.data.stage}`);
    }

    const error = getWorkerError(event.data);
    if (error !== undefined) {
      outputChannel?.error(error);
    }
  };
  const errorListener = (event: ErrorEvent) => {
    outputChannel?.error(event.error instanceof Error ? event.error : new Error(event.message));
  };
  targetWorker.addEventListener("message", messageListener);
  targetWorker.addEventListener("error", errorListener);
  return {
    dispose: () => {
      targetWorker.removeEventListener("message", messageListener);
      targetWorker.removeEventListener("error", errorListener);
    },
  };
}

async function restartServer(): Promise<void> {
  const context = extensionContext;
  if (context === undefined) {
    return;
  }

  outputChannel?.info("Restarting the browser language server.");
  await stopServer();
  await startServer(context);
}

function createClientOptions(
  targetWorker: Worker,
  mapping: BrowserWorkspaceMapping,
): LanguageClientOptions {
  if (outputChannel === undefined) {
    throw new Error("The csls output channel was not initialized.");
  }

  const workspaceFolder = mapping.folders[0];
  return {
    diagnosticPullOptions: {
      onChange: true,
      onFocus: true,
      onSave: true,
      onTabs: true,
    },
    documentSelector: [
      { language: "csharp" },
      { language: "razor" },
    ],
    outputChannel,
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
      didChange: async (event, next) => {
        await next(event);
        await synchronizeTextDocument(targetWorker, mapping, event.document);
      },
      didOpen: async (document, next) => {
        await synchronizeTextDocument(targetWorker, mapping, document);
        await next(document);
      },
      didSave: async (document, next) => {
        await synchronizeTextDocument(targetWorker, mapping, document);
        await next(document);
      },
      workspace: {
        didChangeWorkspaceFolders: async (event, next) => {
          mapping.addFolders(event.added);
          await sendFileUpdates(
            targetWorker,
            await createFolderUpdates(mapping, event.added),
          );
          try {
            await next(event);
          } finally {
            await sendFileUpdates(
              targetWorker,
              event.removed.map((folder) => ({
                kind: "delete" as const,
                path: mapping.getVirtualFolderPath(folder),
              })),
            );
            mapping.removeFolders(event.removed);
          }
        },
        didCreateFiles: async (event, next) => {
          await sendFileUpdates(
            targetWorker,
            await createWriteUpdates(mapping, event.files),
          );
          await next(event);
        },
        didDeleteFiles: async (event, next) => {
          await sendFileUpdates(
            targetWorker,
            event.files.map((uri) => ({
              kind: "delete" as const,
              path: mapping.toVirtualPath(uri),
            })),
          );
          await next(event);
        },
        didRenameFiles: async (event, next) => {
          const updates: BrowserFileUpdate[] = event.files.map((file) => ({
            kind: "delete",
            path: mapping.toVirtualPath(file.oldUri),
          }));
          updates.push(
            ...await createWriteUpdates(
              mapping,
              event.files.map((file) => file.newUri),
            ),
          );
          await sendFileUpdates(targetWorker, updates);
          await next(event);
        },
      },
    },
    synchronize: {
      configurationSection: ["csls", "csharp", "dotnet"],
    },
    uriConverters: {
      code2Protocol: mapping.toProtocolUri,
      protocol2Code: mapping.toCodeUri,
    },
    ...(workspaceFolder === undefined ? {} : { workspaceFolder }),
  };
}

function shouldRunFullSolutionDiagnostics(): boolean {
  const configuration = vscode.workspace.getConfiguration("dotnet.backgroundAnalysis");
  return configuration.get<string>("analyzerDiagnosticsScope", "openFiles") ===
      "fullSolution" ||
    configuration.get<string>("compilerDiagnosticsScope", "openFiles") === "fullSolution";
}

async function synchronizeWorkspace(
  targetWorker: Worker,
  languageServerPort: MessagePort,
  dotnetUri: vscode.Uri,
  mapping: BrowserWorkspaceMapping,
): Promise<void> {
  const runtimeReady = waitForWorkerMessage(targetWorker, "csls/runtimeReady");
  targetWorker.postMessage({
    dotnetUri: dotnetUri.toString(),
    languageServerPort,
    type: "csls/bootstrap",
  }, [languageServerPort]);
  await runtimeReady;
  const files = new Map<string, vscode.Uri>();
  for (const folder of mapping.folders) {
    const uris = await vscode.workspace.findFiles(
      new vscode.RelativePattern(folder, synchronizationInclude),
      synchronizationExclude,
      maximumSynchronizedFiles + 1,
    );
    for (const uri of uris) {
      files.set(uri.toString(), uri);
    }
  }

  if (files.size > maximumSynchronizedFiles) {
    throw new Error(
      `The browser workspace contains more than ${maximumSynchronizedFiles} synchronized files.`,
    );
  }

  let synchronizedBytes = 0;
  const synchronizedFiles = [];
  for (const uri of files.values()) {
    const bytes = await vscode.workspace.fs.readFile(uri);
    synchronizedBytes += bytes.byteLength;
    if (synchronizedBytes > maximumSynchronizedBytes) {
      throw new Error(
        `The browser workspace exceeds the ${maximumSynchronizedBytes}-byte synchronization limit.`,
      );
    }

    synchronizedFiles.push({
      content: decodeText(bytes),
      path: mapping.toVirtualPath(uri),
    });
  }

  const serverReady = waitForWorkerMessage(targetWorker, "csls/ready");
  targetWorker.postMessage({
    files: synchronizedFiles,
    folders: mapping.folders.map((folder) => mapping.getVirtualFolderPath(folder)),
    type: "csls/synchronize",
  });
  await serverReady;
}

type BrowserFileUpdate =
  | { readonly kind: "directory"; readonly path: string }
  | { readonly content: string; readonly kind: "write"; readonly path: string }
  | { readonly kind: "delete"; readonly path: string };

async function synchronizeTextDocument(
  targetWorker: Worker,
  mapping: BrowserWorkspaceMapping,
  document: vscode.TextDocument,
): Promise<void> {
  if (document.uri.scheme === "untitled" || !isSynchronizedFile(document.uri)) {
    return;
  }

  await sendFileUpdates(targetWorker, [{
    content: document.getText(),
    kind: "write",
    path: mapping.toVirtualPath(document.uri),
  }]);
}

async function createFolderUpdates(
  mapping: BrowserWorkspaceMapping,
  folders: readonly vscode.WorkspaceFolder[],
): Promise<BrowserFileUpdate[]> {
  const updates: BrowserFileUpdate[] = [];
  for (const folder of folders) {
    updates.push({
      kind: "directory",
      path: mapping.getVirtualFolderPath(folder),
    });
    const uris = await vscode.workspace.findFiles(
      new vscode.RelativePattern(folder, synchronizationInclude),
      synchronizationExclude,
      maximumSynchronizedFiles + 1,
    );
    updates.push(...await createWriteUpdates(mapping, uris));
  }

  return updates;
}

async function createWriteUpdates(
  mapping: BrowserWorkspaceMapping,
  uris: readonly vscode.Uri[],
): Promise<BrowserFileUpdate[]> {
  const updates: BrowserFileUpdate[] = [];
  for (const uri of uris) {
    await addWriteUpdates(mapping, uri, updates);
    if (updates.length > maximumSynchronizedFiles) {
      throw new Error(
        `A browser workspace update contains more than ${maximumSynchronizedFiles} files.`,
      );
    }
  }

  const synchronizedBytes = updates.reduce(
    (total, update) => total + (update.kind === "write" ? update.content.length * 2 : 0),
    0,
  );
  if (synchronizedBytes > maximumSynchronizedBytes) {
    throw new Error(
      `A browser workspace update exceeds ${maximumSynchronizedBytes} bytes.`,
    );
  }

  return updates;
}

async function addWriteUpdates(
  mapping: BrowserWorkspaceMapping,
  uri: vscode.Uri,
  updates: BrowserFileUpdate[],
): Promise<void> {
  const stat = await vscode.workspace.fs.stat(uri);
  if ((stat.type & vscode.FileType.Directory) !== 0) {
    if (isExcludedDirectory(uri)) {
      return;
    }

    updates.push({ kind: "directory", path: mapping.toVirtualPath(uri) });
    const entries = await vscode.workspace.fs.readDirectory(uri);
    for (const [name] of entries) {
      await addWriteUpdates(mapping, vscode.Uri.joinPath(uri, name), updates);
    }

    return;
  }

  if (!isSynchronizedFile(uri)) {
    return;
  }

  const bytes = await vscode.workspace.fs.readFile(uri);
  updates.push({
    content: decodeText(bytes),
    kind: "write",
    path: mapping.toVirtualPath(uri),
  });
}

async function sendFileUpdates(
  targetWorker: Worker,
  updates: readonly BrowserFileUpdate[],
): Promise<void> {
  if (updates.length === 0) {
    return;
  }

  const requestId = nextControlRequestId++;
  const synchronized = waitForWorkerMessage(
    targetWorker,
    "csls/filesSynchronized",
    requestId,
    fileSynchronizationTimeoutMilliseconds,
  );
  targetWorker.postMessage({
    requestId,
    type: "csls/updateFiles",
    updates,
  });
  await synchronized;
}

function isSynchronizedFile(uri: vscode.Uri): boolean {
  return !isExcludedDirectory(uri) &&
    synchronizedFileExtensions.has(getFileExtension(uri.path));
}

function isExcludedDirectory(uri: vscode.Uri): boolean {
  return uri.path
    .split("/")
    .some((segment) => excludedDirectoryNames.has(segment));
}

function getFileExtension(path: string): string {
  const name = path.slice(path.lastIndexOf("/") + 1);
  const dot = name.indexOf(".");
  return dot < 0 ? "" : name.slice(dot);
}

function decodeText(bytes: Uint8Array): string {
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    return new TextDecoder("utf-16le", { fatal: true }).decode(bytes.subarray(2));
  }

  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    return new TextDecoder("utf-16be", { fatal: true }).decode(bytes.subarray(2));
  }

  const offset = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf
    ? 3
    : 0;
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes.subarray(offset));
}

function waitForWorkerMessage(
  targetWorker: Worker,
  expectedType: string,
  expectedRequestId?: number,
  timeoutMilliseconds = startupTimeoutMilliseconds,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error(`Timed out waiting for ${expectedType} from the csls browser worker.`));
    }, timeoutMilliseconds);
    const messageListener = (event: MessageEvent<unknown>) => {
      if (isWorkerStatusMessage(event.data)) {
        outputChannel?.debug(`Browser worker startup: ${event.data.stage}`);
      }

      const workerError = getWorkerError(event.data);
      if (workerError !== undefined) {
        cleanup();
        reject(workerError);
        return;
      }

      if (isWorkerControlMessage(event.data, expectedType, expectedRequestId)) {
        cleanup();
        resolve();
      }
    };
    const errorListener = (event: ErrorEvent) => {
      cleanup();
      reject(event.error instanceof Error ? event.error : new Error(event.message));
    };
    const cleanup = () => {
      clearTimeout(timeout);
      targetWorker.removeEventListener("message", messageListener);
      targetWorker.removeEventListener("error", errorListener);
    };
    targetWorker.addEventListener("message", messageListener);
    targetWorker.addEventListener("error", errorListener);
  });
}

function isWorkerStatusMessage(value: unknown): value is { readonly stage: string } {
  return typeof value === "object" &&
    value !== null &&
    "type" in value &&
    value.type === "csls/status" &&
    "stage" in value &&
    typeof value.stage === "string";
}

function getWorkerError(value: unknown): Error | undefined {
  if (typeof value !== "object" ||
    value === null ||
    !("type" in value) ||
    value.type !== "csls/error" ||
    !("message" in value) ||
    typeof value.message !== "string") {
    return undefined;
  }

  const error = new Error(value.message);
  if ("stack" in value && typeof value.stack === "string") {
    error.stack = value.stack;
  }

  return error;
}

function isWorkerControlMessage(
  value: unknown,
  expectedType: string,
  expectedRequestId?: number,
): boolean {
  return typeof value === "object" &&
    value !== null &&
    "type" in value &&
    value.type === expectedType &&
    (expectedRequestId === undefined ||
      ("requestId" in value && value.requestId === expectedRequestId));
}
