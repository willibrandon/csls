import { strict as assert } from "node:assert";
import { readFile } from "node:fs/promises";
import * as vscode from "vscode";
import { withTimeout } from "./contract-support";
import { DebuggerWorkbenchUi } from "./debugger-workbench-ui";

const timeoutMilliseconds = 30_000;
const sessionName = "Inspect captured managed dump";

interface StackFrame {
  readonly id: number;
  readonly name: string;
}

interface Message {
  readonly type: string;
  readonly command?: string;
  readonly event?: string;
  readonly success?: boolean;
  readonly body?: {
    readonly reason?: string;
    readonly threadId?: number;
    readonly capabilities?: Record<string, unknown>;
    readonly stackFrames?: readonly StackFrame[];
  };
}

/** Records real editor/adapter exchanges and waits on their arrival. */
class DumpObserver implements vscode.Disposable {
  readonly messages: Message[] = [];
  private readonly changed = new vscode.EventEmitter<void>();

  readonly factory: vscode.DebugAdapterTrackerFactory = {
    createDebugAdapterTracker: (session) => session.name !== sessionName ? undefined : {
      onDidSendMessage: (message: Message) => {
        this.messages.push(message);
        assert(this.messages.length <= 256, "Dump inspection must have bounded protocol traffic.");
        this.changed.fire();
      },
    },
  };

  async waitFor(predicate: (message: Message) => boolean, description: string): Promise<Message> {
    const result = Promise.withResolvers<Message>();
    const find = (): void => {
      const message = this.messages.find(predicate);
      if (message !== undefined) {
        result.resolve(message);
      }
    };
    const subscription = this.changed.event(find);
    try {
      find();
      return await withTimeout(result.promise, timeoutMilliseconds, description);
    } finally {
      subscription.dispose();
    }
  }

  dispose(): void {
    this.changed.dispose();
  }
}

export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert(folder !== undefined, "The isolated editor workspace must be open.");
  const fixture = JSON.parse(await readFile(
    vscode.Uri.joinPath(folder.uri, ".vscode", "dump-fixture.json").fsPath, "utf8",
  )) as { dumpPath: string; binarySearchPaths: string[]; processId: number; moduleName: string };
  assert(fixture.processId > 0);
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The packaged csls extension must be installed.");
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(
    vscode.Uri.joinPath(folder.uri, "Program.cs"),
  ));
  await extension.activate();
  await vscode.commands.executeCommand("csls.refreshSolution");

  const observer = new DumpObserver();
  const tracker = vscode.debug.registerDebugAdapterTrackerFactory("coreclr", observer.factory);
  const started = Promise.withResolvers<vscode.DebugSession>();
  const selected = Promise.withResolvers<vscode.DebugStackFrame>();
  const terminated = Promise.withResolvers<void>();
  let session: vscode.DebugSession | undefined;
  let ui: DebuggerWorkbenchUi | undefined;
  const startListener = vscode.debug.onDidStartDebugSession((candidate) => {
    if (candidate.name === sessionName) {
      session = candidate;
      started.resolve(candidate);
    }
  });
  const frameListener = vscode.debug.onDidChangeActiveStackItem((item) => {
    if (item instanceof vscode.DebugStackFrame && item.session.name === sessionName) {
      selected.resolve(item);
    }
  });
  const stopListener = vscode.debug.onDidTerminateDebugSession((candidate) => {
    if (candidate === session) {
      terminated.resolve();
    }
  });

  try {
    assert.equal(await withTimeout(vscode.debug.startDebugging(folder, {
      name: sessionName,
      type: "coreclr",
      request: "attach",
      dumpPath: fixture.dumpPath,
      binarySearchPaths: fixture.binarySearchPaths,
    }), timeoutMilliseconds, "The installed extension did not open the managed dump."), true);
    const activeSession = await withTimeout(started.promise, timeoutMilliseconds,
      "VS Code did not create the dump debug session.");
    const stopped = await observer.waitFor((message) => message.event === "stopped",
      "The dump adapter did not report a stopped snapshot.");
    assert.equal(stopped.body?.reason, "dump");
    const capabilities = observer.messages.find((message) => message.event === "capabilities");
    assert(capabilities !== undefined);
    assert(observer.messages.indexOf(capabilities) <
      observer.messages.findIndex((message) => message.event === "initialized"),
    "Dump capabilities must arrive before the editor configures the session.");
    assert.equal(capabilities.body?.capabilities?.["supportsModulesRequest"], true);
    assert.equal(capabilities.body?.capabilities?.["supportsRestartRequest"], false);
    assert.equal(capabilities.body?.capabilities?.["supportsSetVariable"], false);

    await observer.waitFor((message) => message.command === "stackTrace" && message.success === true,
      "The editor did not load its dump Call Stack view.");
    await vscode.commands.executeCommand("workbench.view.debug");
    await vscode.commands.executeCommand("workbench.debug.action.focusCallStackView");
    ui = await DebuggerWorkbenchUi.connect(timeoutMilliseconds);
    const stackTree = ui.page.getByRole("treegrid", { name: "Debug Call Stack", exact: true });
    await stackTree.waitFor({ state: "visible" });
    const stoppedThread = stackTree.getByRole("row", { name: /^Thread Managed thread 1 / });
    if (await stoppedThread.getAttribute("aria-expanded") === "false") {
      await stoppedThread.locator(".monaco-tl-twistie").click();
    }
    const fixtureFrame = stackTree.getByRole("row", { name: /Csls.TestProcessHost.DebuggerFixture.WaitForSignal/ });
    await fixtureFrame.click();
    const frame = await withTimeout(selected.promise, timeoutMilliseconds,
      "VS Code did not select a real managed dump stack frame.");
    assert.equal(frame.session.id, activeSession.id);
    assert.equal(frame.threadId, stopped.body?.threadId);
    const editorStack = await observer.waitFor((message) => message.command === "stackTrace" &&
      message.success === true && message.body?.stackFrames?.some((item) => item.id === frame.frameId) === true,
    "The editor's selected frame did not come from the dump stack response.");
    assert(editorStack.body?.stackFrames?.some((item) => item.name.includes("Csls.TestProcessHost")),
      "The editor stack must contain the independently captured managed fixture.");
    await observer.waitFor((message) => message.command === "scopes" && message.success === true,
      "The editor did not load the captured frame's scopes.");
    const variablesTree = ui.page.getByRole("tree", { name: "Debug Variables", exact: true });
    await variablesTree.waitFor({ state: "visible" });
    for (const [scope, expected] of [
      ["Arguments", "number, value 42"],
      ["Locals", "localNumber, value 43"],
    ] as const) {
      const row = variablesTree.getByRole("treeitem", { name: `Scope ${scope}`, exact: true });
      await row.waitFor({ state: "visible" });
      if (await row.getAttribute("aria-expanded") !== "true") {
        await row.locator(".monaco-tl-twistie").click();
      }
      await variablesTree.getByRole("treeitem", { name: expected, exact: true })
        .waitFor({ state: "visible" });
    }
    await variablesTree.getByRole("treeitem", { name: "localLong, value 44", exact: true })
      .waitFor({ state: "visible" });
    assert.equal(await variablesTree.locator(".error").count(), 0,
      "The captured frame's Variables pane must render values without an error scope.");
    const threads = await activeSession.customRequest("threads") as {
      threads: { id: number; name: string }[];
    };
    assert(threads.threads.some((thread) => thread.id === frame.threadId));
    const firstPage = await activeSession.customRequest("stackTrace", {
      threadId: frame.threadId, startFrame: 0, levels: 1,
    }) as { stackFrames: StackFrame[]; totalFrames: number };
    assert.equal(firstPage.stackFrames.length, 1);
    assert.equal(firstPage.stackFrames[0]?.id, editorStack.body?.stackFrames?.[0]?.id);
    assert(firstPage.totalFrames > 1);
    const secondPage = await activeSession.customRequest("stackTrace", {
      threadId: frame.threadId, startFrame: 1, levels: 1,
    }) as { stackFrames: StackFrame[]; totalFrames: number };
    assert.equal(secondPage.stackFrames.length, 1);
    assert.equal(secondPage.totalFrames, firstPage.totalFrames);
    assert.notEqual(secondPage.stackFrames[0]?.id, firstPage.stackFrames[0]?.id);
    const modules = await activeSession.customRequest("modules", {
      startModule: 0, moduleCount: 100,
    }) as { modules: { name: string }[]; totalModules: number };
    assert(modules.totalModules > 0);
    assert(modules.modules.length <= 100);
    assert(modules.modules.some((module) => module.name === fixture.moduleName));
    assert.deepEqual(observer.messages.filter((message) => message.type === "response" &&
      message.success === false), [], "The real editor's dump workflow must succeed.");
    await vscode.debug.stopDebugging(activeSession);
    await withTimeout(terminated.promise, timeoutMilliseconds,
      "The editor did not terminate its dump session.");
    assert.equal(observer.messages.filter((message) => message.event === "terminated").length, 1);
    assert.equal(observer.messages.filter((message) => message.event === "exited").length, 0,
      "Closing a dump must not report a new exit for its historical process.");
  } catch (error) {
    const snapshot = await ui?.captureDiagnostics() ?? "";
    throw new Error(`${String(error)}\nDump UI: ${snapshot}\nDump protocol: ${JSON.stringify(observer.messages)}`,
      { cause: error });
  } finally {
    if (session !== undefined && vscode.debug.activeDebugSession === session) {
      await vscode.debug.stopDebugging(session);
      await withTimeout(terminated.promise, timeoutMilliseconds, "Dump cleanup did not finish.");
    }
    stopListener.dispose();
    frameListener.dispose();
    startListener.dispose();
    tracker.dispose();
    observer.dispose();
    await ui?.dispose();
  }
}
