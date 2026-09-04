import { strict as assert } from "node:assert";
import { access, readFile } from "node:fs/promises";
import { setTimeout as delay } from "node:timers/promises";
import * as vscode from "vscode";
import { ResultsViewUi } from "./results-view-ui";

const timeoutMilliseconds = 30_000;
const programText = `GC.KeepAlive(Enumerable.Empty<int>().ToArray());
PagingEnumerable values = new();
GC.KeepAlive(values);
`;
const enumerableText = `using System.Collections;
using System.Collections.Generic;
using System.IO;

internal sealed class PagingEnumerable : IEnumerable<int>
{
    public IEnumerator<int> GetEnumerator()
    {
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "enumerations.txt"), "enumerated\\n");
        for (int index = 0; index < 205; index++)
        {
            yield return index;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
`;

interface Variable {
  readonly name: string;
  readonly value: string;
  readonly type?: string;
  readonly variablesReference: number;
  readonly namedVariables?: number;
  readonly indexedVariables?: number;
  readonly presentationHint?: { readonly lazy?: boolean };
}

interface VariablesRequest {
  readonly seq: number;
  readonly arguments: {
    readonly variablesReference: number;
    readonly filter?: string;
    readonly start?: number;
    readonly count?: number;
  };
}

interface VariablesExchange {
  readonly request: VariablesRequest;
  readonly variables: readonly Variable[];
}

/** Observes actual VS Code requests without issuing or replacing adapter operations. */
class VariablesObserver {
  readonly requests = new Map<number, VariablesRequest>();
  readonly exchanges: VariablesExchange[] = [];
  readonly failures: unknown[] = [];
  readonly lifecycleEvents: Record<string, unknown>[] = [];
  readonly inspectionMessages: Record<string, unknown>[] = [];

  readonly factory: vscode.DebugAdapterTrackerFactory = {
    createDebugAdapterTracker: () => ({
      onWillReceiveMessage: (message: unknown) => {
        this.captureInspectionMessage(message, "request");
        if (isMessage(message, "request", "variables")) {
          const request = message as unknown as VariablesRequest;
          this.requests.set(request.seq, request);
        }
      },
      onDidSendMessage: (message: unknown) => {
        this.captureInspectionMessage(message, "response");
        if (typeof message === "object" && message !== null &&
            "type" in message && message.type === "event" && "event" in message &&
            ["invalidated", "continued", "stopped", "exited", "terminated"].includes(String(message.event))) {
          this.lifecycleEvents.push(message as Record<string, unknown>);
          if (this.lifecycleEvents.length > 32) {
            this.lifecycleEvents.shift();
          }
        }

        if (!isMessage(message, "response", "variables")) {
          return;
        }

        if (message["success"] !== true) {
          this.failures.push(message);
          return;
        }

        const request = this.requests.get(Number(message["request_seq"]));
        assert(request !== undefined, "Every observed response must match a VS Code request.");
        const body = message["body"] as { variables: readonly Variable[] };
        this.exchanges.push({ request, variables: body.variables });
      },
    }),
  };

  async waitFor(
    predicate: (exchange: VariablesExchange) => boolean,
    description: string,
  ): Promise<VariablesExchange> {
    let exchange: VariablesExchange | undefined;
    await waitUntil(() => {
      this.assertNoFailures();
      exchange = this.exchanges.find(predicate);
      return exchange !== undefined;
    }, () => `${description}\nObserved variable exchanges: ${JSON.stringify(this.exchanges)}`);
    assert(exchange !== undefined);
    return exchange;
  }

  assertNoFailures(): void {
    assert.deepEqual(this.failures, [], "The real Variables view must not request stale handles.");
  }

  private captureInspectionMessage(message: unknown, type: "request" | "response"): void {
    if (isMessage(message, type, "stackTrace") || isMessage(message, type, "scopes")) {
      if (type === "response" && message["success"] !== true) {
        this.failures.push(message);
      }
      this.inspectionMessages.push(message);
      if (this.inspectionMessages.length > 32) {
        this.inspectionMessages.shift();
      }
    }
  }
}

export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert(folder !== undefined, "The isolated VS Code test workspace must be open.");
  const program = vscode.Uri.joinPath(folder.uri, "Program.cs");
  const project = vscode.Uri.joinPath(folder.uri, "App", "Fixture.csproj");
  const enumerationLog = vscode.Uri.joinPath(
    folder.uri, "App", "bin", "Debug", "net10.0", "enumerations.txt",
  );
  const projectText = new TextDecoder().decode(await vscode.workspace.fs.readFile(project));
  assert(projectText.includes("</ItemGroup>"), "The real application project must define its sources.");
  await vscode.workspace.fs.writeFile(project, new TextEncoder().encode(projectText.replace(
    "</ItemGroup>",
    '<Compile Include="../PagingEnumerable.cs" /></ItemGroup>',
  )));
  await vscode.workspace.fs.writeFile(program, new TextEncoder().encode(programText));
  await vscode.workspace.fs.writeFile(
    vscode.Uri.joinPath(folder.uri, "PagingEnumerable.cs"),
    new TextEncoder().encode(enumerableText),
  );

  const configuration = vscode.workspace.getConfiguration("debug");
  const previousAutoExpand = configuration.inspect<string>("autoExpandLazyVariables")?.globalValue;
  await configuration.update("autoExpandLazyVariables", "off", vscode.ConfigurationTarget.Global);
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The packaged csls extension must be installed.");
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(program));
  await extension.activate();
  await vscode.commands.executeCommand("csls.refreshSolution");

  const observer = new VariablesObserver();
  const tracker = vscode.debug.registerDebugAdapterTrackerFactory("coreclr", observer.factory);
  const breakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(program, new vscode.Position(2, 0)),
  );
  vscode.debug.addBreakpoints([breakpoint]);
  let session: vscode.DebugSession | undefined;
  let ui: ResultsViewUi | undefined;
  let terminated = false;
  const startListener = vscode.debug.onDidStartDebugSession((candidate) => {
    if (candidate.type === "coreclr") {
      session = candidate;
    }
  });
  const terminationListener = vscode.debug.onDidTerminateDebugSession((candidate) => {
    if (candidate === session) {
      terminated = true;
    }
  });

  try {
    await vscode.commands.executeCommand("csls.debug", {
      projectPath: project.fsPath,
      workspaceRoot: folder.uri.fsPath,
    });
    await waitUntil(
      () => session !== undefined && vscode.debug.activeStackItem instanceof vscode.DebugStackFrame,
      () => "The real coreclr session did not reach its source breakpoint.",
    );
    await vscode.commands.executeCommand("workbench.debug.action.focusVariablesView");
    ui = await ResultsViewUi.connect(timeoutMilliseconds);
    await ui.expandLocals();
    const locals = await observer.waitFor(
      (exchange) => exchange.variables.some((variable) => variable.name === "values"),
      "The Variables view did not fetch the stopped frame's locals.",
    );
    assert.deepEqual(locals.variables.map((variable) => variable.name), ["values"]);
    const enumerable = locals.variables[0]!;
    assert(enumerable.variablesReference > 0);
    await assert.rejects(access(enumerationLog.fsPath), { code: "ENOENT" });

    await ui.expandEnumerable();
    const discovery = await observer.waitFor(
      (exchange) => exchange.request.arguments.variablesReference === enumerable.variablesReference,
      "Expanding the local did not request its real child variables.",
    );
    assert.deepEqual(discovery.variables.map((variable) => variable.name), ["Results View"]);
    const lazy = discovery.variables[0]!;
    assert.equal(lazy.presentationHint?.lazy, true);
    await ui.resolveResultsView();
    const resolution = await observer.waitFor(
      (exchange) => exchange.request.arguments.variablesReference === lazy.variablesReference,
      "VS Code did not resolve the Results View through its lazy-variable mechanism.",
    );
    assert.equal(resolution.variables.length, 1, "Lazy resolution must return one replacement variable.");
    const snapshot = resolution.variables[0]!;
    assert.equal(snapshot.name, "Results View");
    assert.notEqual(snapshot.presentationHint?.lazy, true);
    assert(snapshot.variablesReference > 0);
    assert.notEqual(snapshot.variablesReference, lazy.variablesReference);
    assert.equal(snapshot.indexedVariables, 205);
    assert.equal(snapshot.namedVariables, 0);

    // The resolved row must survive invalidation. Expanding it builds VS Code's
    // real virtual chunks, whose expansion must use the adopted snapshot handle.
    await ui.expandSnapshot();
    await ui.expandChunk(100, 199);
    const middle = await observer.waitFor(
      (exchange) => isPage(exchange, snapshot.variablesReference, 100, 100),
      "The Variables view did not fetch the middle chunk through the adopted snapshot handle.",
    );
    assertPage(middle, 100, 100);

    await ui.collapseChunk(100, 199);
    await ui.expandChunk(200, 204);
    const final = await observer.waitFor(
      (exchange) => isPage(exchange, snapshot.variablesReference, 200, 5),
      "The Variables view did not fetch the final partial chunk through the same snapshot handle.",
    );
    assertPage(final, 200, 5);
    assert.equal(await readFile(enumerationLog.fsPath, "utf8"), "enumerated\n");
    const lazyReferences = new Set(observer.exchanges.flatMap((exchange) =>
      exchange.variables.filter((variable) => variable.presentationHint?.lazy)
        .map((variable) => variable.variablesReference)));
    assert.equal(
      [...observer.requests.values()].filter((request) =>
        lazyReferences.has(request.arguments.variablesReference)).length,
      1,
      "UI refresh and snapshot paging must not enumerate the target again.",
    );
    observer.assertNoFailures();
    assert.equal(terminated, false, "The target must remain stopped while paging its snapshot.");
    await vscode.commands.executeCommand("workbench.action.debug.continue");
    await waitUntil(() => terminated, () => "The debugger-owned target did not exit after continuing.");
  } catch (error) {
    console.error("Observed Variables-view protocol:\n" + JSON.stringify({
      requests: [...observer.requests.values()].slice(-32),
      exchanges: observer.exchanges.slice(-32),
      failures: observer.failures.slice(-32),
      lifecycleEvents: observer.lifecycleEvents,
      inspectionMessages: observer.inspectionMessages,
    }, null, 2).slice(0, 32_768));
    if (ui !== undefined) {
      console.error(await ui.captureDiagnostics());
    }

    throw error;
  } finally {
    try {
      if (session !== undefined && !terminated) {
        await vscode.debug.stopDebugging(session);
        await waitUntil(() => terminated, () => "The debugger-owned target did not stop during cleanup.");
      }
    } finally {
      tracker.dispose();
      startListener.dispose();
      terminationListener.dispose();
      vscode.debug.removeBreakpoints([breakpoint]);
      await configuration.update(
        "autoExpandLazyVariables", previousAutoExpand, vscode.ConfigurationTarget.Global,
      );
      await ui?.dispose();
    }
  }
}

function isPage(exchange: VariablesExchange, reference: number, start: number, count: number): boolean {
  const request = exchange.request.arguments;
  return request.variablesReference === reference && request.filter === "indexed" &&
    request.start === start && request.count === count;
}

function assertPage(exchange: VariablesExchange, start: number, count: number): void {
  assert.deepEqual(exchange.variables.map((variable) => [variable.name, variable.value, variable.type]),
    Array.from({ length: count }, (_, offset) => {
      const index = start + offset;
      return [`[${index}]`, String(index), "int"];
    }));
}

function isMessage(value: unknown, type: string, command: string): value is Record<string, unknown> {
  return typeof value === "object" && value !== null &&
    "type" in value && value.type === type && "command" in value && value.command === command;
}

async function waitUntil(condition: () => boolean, message: () => string): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (Date.now() < deadline) {
    if (condition()) {
      return;
    }

    await delay(25);
  }

  throw new Error(message());
}
