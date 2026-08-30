import { type ChildProcess, spawn, spawnSync } from "node:child_process";
import * as vscode from "vscode";
import type { ProcessExecutionResult } from "./processExecutionResult.js";
import { waitForProcessCompletion } from "./processLifecycle.mjs";

export class ProcessExecutor implements vscode.Disposable {
  private readonly activeProcesses = new Set<ChildProcess>();
  private operationPromise: Promise<void> = Promise.resolve();
  private disposed = false;

  constructor(
    private readonly executablePath: string,
    private readonly outputChannel: vscode.LogOutputChannel,
    private readonly displayName = "dotnet",
  ) {}

  async execute(
    arguments_: readonly string[],
    cwd: string,
    cancellationToken?: vscode.CancellationToken,
    revealOutput = false,
    logOutput = true,
  ): Promise<ProcessExecutionResult> {
    const operation = this.operationPromise.then(() => this.executeCore(
      arguments_,
      cwd,
      cancellationToken,
      revealOutput,
      logOutput,
    ));
    this.operationPromise = operation.then(
      () => undefined,
      () => undefined,
    );
    return operation;
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    for (const childProcess of this.activeProcesses) {
      terminateProcessTree(childProcess);
    }
  }

  private async executeCore(
    arguments_: readonly string[],
    cwd: string,
    cancellationToken?: vscode.CancellationToken,
    revealOutput = false,
    logOutput = true,
  ): Promise<ProcessExecutionResult> {
    if (this.disposed) {
      throw new vscode.CancellationError();
    }

    if (revealOutput) {
      this.outputChannel.show(true);
    }

    if (logOutput) {
      this.outputChannel.appendLine(
        `> ${this.displayName} ${arguments_.map(quoteArgument).join(" ")}`,
      );
    }
    const childProcess = spawn(this.executablePath, arguments_, {
      cwd,
      detached: process.platform !== "win32",
      env: {
        ...process.env,
        MSBUILDDISABLENODEREUSE: "1",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    this.activeProcesses.add(childProcess);
    childProcess.stdout.setEncoding("utf8");
    childProcess.stderr.setEncoding("utf8");
    let stdout = "";
    let stderr = "";
    let pendingStandardOutput = "";
    let pendingStandardError = "";
    childProcess.stdout.on("data", (value: string) => {
      stdout += value;
      if (logOutput) {
        pendingStandardOutput = appendCompleteProcessLines(
          pendingStandardOutput,
          value,
          this.outputChannel,
        );
      }
    });
    childProcess.stderr.on("data", (value: string) => {
      stderr += value;
      if (logOutput) {
        pendingStandardError = appendCompleteProcessLines(
          pendingStandardError,
          value,
          this.outputChannel,
        );
      }
    });
    const cancellation = cancellationToken?.onCancellationRequested(() => {
      terminateProcessTree(childProcess);
    });
    try {
      const exitCode = await waitForProcessCompletion(childProcess);
      if (this.disposed || cancellationToken?.isCancellationRequested === true) {
        throw new vscode.CancellationError();
      }

      if (logOutput) {
        appendPendingProcessLine(pendingStandardOutput, this.outputChannel);
        appendPendingProcessLine(pendingStandardError, this.outputChannel);
      }
      return { exitCode, stderr, stdout };
    } finally {
      cancellation?.dispose();
      this.activeProcesses.delete(childProcess);
    }
  }
}

function terminateProcessTree(childProcess: ChildProcess): void {
  if (
    childProcess.pid === undefined ||
    childProcess.exitCode !== null ||
    childProcess.signalCode !== null
  ) {
    return;
  }

  if (process.platform === "win32") {
    spawnSync(
      "taskkill.exe",
      ["/pid", String(childProcess.pid), "/T", "/F"],
      { stdio: "ignore", windowsHide: true },
    );
    return;
  }

  try {
    process.kill(-childProcess.pid, "SIGTERM");
  } catch {
    childProcess.kill("SIGTERM");
  }
}

function appendCompleteProcessLines(
  pending: string,
  value: string,
  outputChannel: vscode.LogOutputChannel,
): string {
  const lines = `${pending}${value}`.split(/\r\n|\n|\r/u);
  const remainder = lines.pop() ?? "";
  for (const line of lines) {
    appendPendingProcessLine(line, outputChannel);
  }

  return remainder;
}

function appendPendingProcessLine(
  value: string,
  outputChannel: vscode.LogOutputChannel,
): void {
  if (value.length > 0) {
    outputChannel.appendLine(value);
  }
}

function quoteArgument(argument: string): string {
  return /[\s"]/u.test(argument) ? JSON.stringify(argument) : argument;
}
