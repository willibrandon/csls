import { spawn } from "node:child_process";
import * as vscode from "vscode";
import type { ProcessExecutionResult } from "./processExecutionResult.js";

export class ProcessExecutor {
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
  ): Promise<ProcessExecutionResult> {
    if (revealOutput) {
      this.outputChannel.show(true);
    }

    this.outputChannel.appendLine(
      `> ${this.displayName} ${arguments_.map(quoteArgument).join(" ")}`,
    );
    const childProcess = spawn(this.executablePath, arguments_, {
      cwd,
      env: {
        ...process.env,
        MSBUILDDISABLENODEREUSE: "1",
      },
      stdio: ["ignore", "pipe", "pipe"],
    });
    childProcess.stdout.setEncoding("utf8");
    childProcess.stderr.setEncoding("utf8");
    let stdout = "";
    let stderr = "";
    childProcess.stdout.on("data", (value: string) => {
      stdout += value;
      this.outputChannel.append(value);
    });
    childProcess.stderr.on("data", (value: string) => {
      stderr += value;
      this.outputChannel.append(value);
    });
    const cancellation = cancellationToken?.onCancellationRequested(() => {
      childProcess.kill();
    });
    try {
      const exitCode = await new Promise<number | null>((resolve, reject) => {
        childProcess.once("error", reject);
        childProcess.once("exit", resolve);
      });
      return { exitCode, stderr, stdout };
    } finally {
      cancellation?.dispose();
    }
  }
}

function quoteArgument(argument: string): string {
  return /[\s"]/u.test(argument) ? JSON.stringify(argument) : argument;
}
