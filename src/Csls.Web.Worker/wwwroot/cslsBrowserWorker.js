globalThis.onmessage = (event) => {
  if (event.data?.type !== "csls/bootstrap") {
    return;
  }

  delete globalThis.onmessage;
  void initialize(event.data.dotnetUri, event.data.languageServerPort).catch((error) => {
    globalThis.postMessage({
      message: error instanceof Error ? error.message : String(error),
      stack: error instanceof Error ? error.stack : undefined,
      type: "csls/error",
    });
  });
};

globalThis.addEventListener("error", (event) => {
  reportError(event.error ?? new Error(event.message));
});
globalThis.addEventListener("unhandledrejection", (event) => {
  reportError(event.reason ?? new Error("Unhandled browser worker rejection."));
});

async function initialize(dotnetUri, languageServerPort) {
  if (!(languageServerPort instanceof MessagePort)) {
    throw new Error("The csls language-server message port was not provided.");
  }

  globalThis.postMessage({ stage: "loadingRuntime", type: "csls/status" });
  const { dotnet } = await import(dotnetUri);
  globalThis.postMessage({ stage: "creatingRuntime", type: "csls/status" });
  const runtime = await dotnet
    .withModuleConfig({
      onAbort: (error) => reportError(error ?? new Error("The .NET runtime aborted.")),
      onExit: (code) => reportError(new Error(`The .NET runtime exited with code ${code}.`)),
    })
    .create();
  globalThis.postMessage({ stage: "loadingExports", type: "csls/status" });
  runtime.setModuleImports("cslsBrowserWorker.js", {
    status: {
      report(stage) {
        globalThis.postMessage({ stage, type: "csls/status" });
      },
    },
    transport: {
      send(message) {
        languageServerPort.postMessage(JSON.parse(message));
      },
      sendResult(requestId, result) {
        languageServerPort.postMessage({
          id: JSON.parse(requestId),
          jsonrpc: "2.0",
          result: JSON.parse(result),
        });
      },
      sendInitializeResult(requestId, supportsRefactor, version) {
        const fileOperationFilters = [
          {
            pattern: {
              glob: "**/*.{cs,csx,cshtml,razor,csproj,sln,slnx,props,targets,ruleset,globalconfig}",
              matches: "file",
              options: { ignoreCase: false },
            },
            scheme: "file",
          },
          {
            pattern: {
              glob: "**/{global.json,packages.config,NuGet.config,.editorconfig}",
              matches: "file",
              options: { ignoreCase: false },
            },
            scheme: "file",
          },
          {
            pattern: {
              glob: "**",
              matches: "folder",
              options: { ignoreCase: false },
            },
            scheme: "file",
          },
        ];
        const serverInfo = { name: "csls" };
        if (version !== null) {
          serverInfo.version = version;
        }

        languageServerPort.postMessage({
          id: JSON.parse(requestId),
          jsonrpc: "2.0",
          result: {
            capabilities: {
              callHierarchyProvider: true,
              codeLensProvider: { resolveProvider: true },
              codeActionProvider: {
                codeActionKinds: supportsRefactor
                  ? ["quickfix", "refactor", "source.organizeImports"]
                  : ["quickfix", "source.organizeImports"],
                resolveProvider: false,
              },
              completionProvider: {
                resolveProvider: true,
                triggerCharacters: [".", "(", "#", "\"", "<", "/"],
              },
              declarationProvider: true,
              definitionProvider: true,
              diagnosticProvider: {
                identifier: "csls",
                interFileDependencies: true,
                workspaceDiagnostics: true,
              },
              documentFormattingProvider: true,
              documentHighlightProvider: true,
              documentLinkProvider: { resolveProvider: false },
              documentOnTypeFormattingProvider: {
                firstTriggerCharacter: "}",
                moreTriggerCharacter: [";", "\n"],
              },
              documentRangeFormattingProvider: true,
              documentSymbolProvider: true,
              experimental: { csharp: { metadataUris: true } },
              foldingRangeProvider: true,
              hoverProvider: true,
              implementationProvider: true,
              inlayHintProvider: { resolveProvider: true },
              linkedEditingRangeProvider: true,
              monikerProvider: true,
              positionEncoding: "utf-16",
              referencesProvider: true,
              renameProvider: { prepareProvider: true },
              selectionRangeProvider: true,
              semanticTokensProvider: {
                full: { delta: true },
                legend: {
                  tokenModifiers: ["static", "deprecated", "reassigned"],
                  tokenTypes: [
                    "namespace",
                    "type",
                    "class",
                    "enum",
                    "interface",
                    "struct",
                    "typeParameter",
                    "parameter",
                    "variable",
                    "property",
                    "enumMember",
                    "event",
                    "function",
                    "method",
                    "macro",
                    "keyword",
                    "modifier",
                    "comment",
                    "string",
                    "number",
                    "regexp",
                    "operator",
                    "decorator",
                    "label",
                  ],
                },
                range: false,
              },
              signatureHelpProvider: {
                retriggerCharacters: [")"],
                triggerCharacters: ["(", ","],
              },
              textDocumentSync: {
                change: 2,
                openClose: true,
                save: true,
                willSaveWaitUntil: true,
              },
              typeDefinitionProvider: true,
              typeHierarchyProvider: true,
              workspace: {
                fileOperations: {
                  didCreate: { filters: fileOperationFilters },
                  didDelete: { filters: fileOperationFilters },
                  didRename: { filters: fileOperationFilters },
                },
                workspaceFolders: {
                  changeNotifications: true,
                  supported: true,
                },
              },
              workspaceSymbolProvider: { resolveProvider: true },
            },
            serverInfo,
          },
        });
      },
      sendHoverResult(
        requestId,
        hasHover,
        kind,
        value,
        hasRange,
        startLine,
        startCharacter,
        endLine,
        endCharacter,
      ) {
        let result = null;
        if (hasHover) {
          result = { contents: { kind, value } };
          if (hasRange) {
            result.range = {
              end: { character: endCharacter, line: endLine },
              start: { character: startCharacter, line: startLine },
            };
          }
        }

        languageServerPort.postMessage({
          id: JSON.parse(requestId),
          jsonrpc: "2.0",
          result,
        });
      },
    },
  });

  const config = runtime.getConfig();
  const assemblyExports = await runtime.getAssemblyExports(config.mainAssemblyName);
  const host = assemblyExports.Csls.Web.Worker.BrowserLanguageServerHost;
  globalThis.postMessage({ stage: "loadingReferences", type: "csls/status" });
  await synchronizeReferences([
    ...(config.resources.coreAssembly ?? []),
    ...(config.resources.assembly ?? []),
  ], host);
  globalThis.postMessage({ stage: "runtimeReady", type: "csls/status" });

  globalThis.addEventListener("message", (event) => {
    void receiveControlMessage(event, host).catch(reportError);
  });
  languageServerPort.addEventListener("message", (event) => {
    void receiveLanguageServerMessage(event, host).catch(reportError);
  });
  languageServerPort.start();

  globalThis.postMessage({ type: "csls/runtimeReady" });
}

async function receiveControlMessage(event, host) {
    if (event.data?.type === "csls/synchronize") {
      globalThis.postMessage({ stage: "synchronizingWorkspace", type: "csls/status" });
      for (const folder of event.data.folders) {
        host.SynchronizeDirectory(folder);
      }

      for (const file of event.data.files) {
        host.SynchronizeFile(file.path, file.content);
      }

      globalThis.postMessage({ stage: "startingServer", type: "csls/status" });
      host.Start();
      globalThis.postMessage({ stage: "serverReady", type: "csls/status" });
      globalThis.postMessage({ type: "csls/ready" });
      return;
    }

    if (event.data?.type === "csls/updateFiles") {
      for (const update of event.data.updates) {
        if (update.kind === "write") {
          host.SynchronizeFile(update.path, update.content);
        } else if (update.kind === "directory") {
          host.SynchronizeDirectory(update.path);
        } else if (update.kind === "delete") {
          host.DeletePath(update.path);
        }
      }

      globalThis.postMessage({
        requestId: event.data.requestId,
        type: "csls/filesSynchronized",
      });
    }
}

async function receiveLanguageServerMessage(event, host) {

    if (event.data?.jsonrpc !== "2.0") {
      return;
    }

    const message = event.data;
    const requestId = Object.hasOwn(message, "id") ? JSON.stringify(message.id) : null;
    const parameters = Object.hasOwn(message, "params") ? JSON.stringify(message.params) : null;
    const result = Object.hasOwn(message, "result") ? JSON.stringify(message.result) : null;
    const error = Object.hasOwn(message, "error") ? JSON.stringify(message.error) : null;
    await host.ReceiveAsync(
      typeof message?.method === "string" ? message.method : null,
      requestId,
      message.params ?? null,
      parameters,
      result,
      error,
    );
}

function reportError(error) {
  globalThis.postMessage({
    message: error instanceof Error ? error.message : String(error),
    stack: error instanceof Error ? error.stack : undefined,
    type: "csls/error",
  });
}

async function synchronizeReferences(assets, host) {
  const referenceAssets = assets.filter((asset) => {
    const name = asset.virtualPath;
    return typeof name === "string" && (name === "Microsoft.CSharp.dll" ||
      name === "mscorlib.dll" ||
      name === "netstandard.dll" ||
      name.startsWith("System."));
  });
  const batchSize = 8;
  for (let index = 0; index < referenceAssets.length; index += batchSize) {
    const batch = referenceAssets.slice(index, index + batchSize);
    const references = await Promise.all(batch.map(async (asset) => {
      const response = await fetch(asset.resolvedUrl, {
        cache: asset.cache,
        integrity: asset.hash,
      });
      if (!response.ok) {
        throw new Error(`Failed to load ${asset.virtualPath}: ${response.status}`);
      }

      return {
        content: new Uint8Array(await response.arrayBuffer()),
        fileName: asset.virtualPath,
      };
    }));
    for (const reference of references) {
      host.SynchronizeReference(reference.fileName, reference.content);
    }
  }
}
