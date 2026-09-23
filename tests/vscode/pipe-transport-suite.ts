import { strict as assert } from "node:assert";
import { readFile, symlink } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import * as vscode from "vscode";
import { withTimeout } from "./contract-support";

const timeoutMilliseconds = 30_000;
const sessionName = "Pipe transport target";
const markerName = "CSLS_PIPE_TRANSPORT_MARKER";
const markerValue = "pipe-transport-target-ok";

interface DebuggerApi {
  readonly serverPath: string;
  readonly runtimePath: string;
}

interface DapMessage {
  readonly type: string;
  readonly event?: string;
  readonly command?: string;
  readonly success?: boolean;
  readonly body?: { readonly exitCode?: number; readonly output?: string };
}

/** Exercises the installed VS Code adapter descriptor through a real shell and target process. */
export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  assert(folder !== undefined, "The isolated editor workspace must be open.");
  const fixture = JSON.parse(await readFile(
    vscode.Uri.joinPath(folder.uri, ".vscode", "pipe-transport-fixture.json").fsPath, "utf8",
  )) as { readonly program: string };
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The packaged csls extension must be installed.");
  const api = await extension.activate() as DebuggerApi;

  const linkedDirectory = join(folder.uri.fsPath, ".vscode", "adapter'pipe");
  await symlink(dirname(api.serverPath), linkedDirectory, "dir");
  const debuggerPath = join(linkedDirectory, basename(api.serverPath));
  const messages: DapMessage[] = [];
  const terminated = Promise.withResolvers<void>();
  const tracker = vscode.debug.registerDebugAdapterTrackerFactory("coreclr", {
    createDebugAdapterTracker: (session) => session.name !== sessionName ? undefined : {
      onDidSendMessage: (message: DapMessage) => {
        assert(messages.length < 256, "Pipe transport must have bounded protocol traffic.");
        messages.push(message);
        if (message.event === "terminated") {
          terminated.resolve();
        }
      },
    },
  });
  let session: vscode.DebugSession | undefined;
  let invalidSessionStarted = false;
  const started = vscode.debug.onDidStartDebugSession((candidate) => {
    if (candidate.name === "Invalid pipe transport") {
      invalidSessionStarted = true;
    }
    if (candidate.name === sessionName) {
      session = candidate;
    }
  });
  try {
    assert.equal(await withTimeout(vscode.debug.startDebugging(folder, {
      name: sessionName,
      type: "coreclr",
      request: "launch",
      program: fixture.program,
      runtimeHost: api.runtimePath,
      args: ["--print-environment-and-exit", markerName, "27"],
      noDebug: true,
      pipeTransport: {
        pipeProgram: "/bin/sh",
        pipeArgs: ["-c", "${debuggerCommand}"],
        debuggerPath,
        ...(debuggerPath.endsWith(".dll") ? { debuggerRuntimePath: api.runtimePath } : {}),
        pipeCwd: folder.uri.fsPath,
        pipeEnv: { [markerName]: markerValue },
      },
    }), timeoutMilliseconds, "VS Code did not start the pipe-backed adapter."), true);
    await withTimeout(terminated.promise, timeoutMilliseconds,
      "The target did not finish through the pipe-backed adapter.");
    assert(messages.some((message) => message.type === "event" && message.event === "process"),
      "The pipe-backed adapter must report its real target process.");
    assert(messages.some((message) => message.event === "output" &&
      message.body?.output?.includes(markerValue) === true),
    "The target must inherit the pipe program's configured environment.");
    assert(messages.some((message) => message.event === "exited" && message.body?.exitCode === 27),
      "The target's actual exit code must cross the pipe.");
    assert.deepEqual(messages.filter((message) => message.type === "response" &&
      message.success === false), [], "The pipe-backed debug session must complete without DAP errors.");

    const invalidStart = await vscode.debug.startDebugging(folder, {
      name: "Invalid pipe transport",
      type: "coreclr",
      request: "launch",
      program: fixture.program,
      noDebug: true,
      pipeTransport: {
        pipeProgram: "/bin/sh",
        pipeArgs: ["-c", "prefix${debuggerCommand}"],
        debuggerPath,
      },
    }).catch(() => false);
    assert.equal(invalidStart, false,
      "The editor must reject an inline adapter-command substitution.");
    assert.equal(invalidSessionStarted, false,
      "A malformed pipe transport must not start a debug session.");
  } catch (error) {
    throw new Error(`${String(error)}\nPipe protocol: ${JSON.stringify(messages)}`, { cause: error });
  } finally {
    if (session !== undefined && vscode.debug.activeDebugSession === session) {
      await vscode.debug.stopDebugging(session);
    }
    started.dispose();
    tracker.dispose();
  }
}
