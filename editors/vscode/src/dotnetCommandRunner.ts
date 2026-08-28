import { dirname } from "node:path";
import * as vscode from "vscode";
import { DotnetProjectInspector } from "./dotnetProjectInspector.js";
import { ProcessExecutor } from "./processExecutor.js";

export class DotnetCommandRunner {
  private readonly executor: ProcessExecutor;
  private readonly inspector: DotnetProjectInspector;

  constructor(
    private readonly dotnetPath: string,
    outputChannel: vscode.LogOutputChannel,
  ) {
    this.executor = new ProcessExecutor(dotnetPath, outputChannel);
    this.inspector = new DotnetProjectInspector(this.executor);
  }

  async build(projectPath: string): Promise<void> {
    await this.execute(["build", projectPath], dirname(projectPath));
  }

  async restore(projectPath: string): Promise<void> {
    await this.execute(["restore", projectPath], dirname(projectPath));
  }

  async debug(projectPath: string): Promise<void> {
    await this.build(projectPath);
    const properties = await this.inspector.inspect(projectPath);
    if (properties.TargetPath === undefined || properties.TargetPath.length === 0) {
      throw new Error(`The project did not provide a debug target: ${projectPath}`);
    }

    const started = await vscode.debug.startDebugging(
      vscode.workspace.getWorkspaceFolder(vscode.Uri.file(projectPath)),
      {
        cwd: dirname(projectPath),
        justMyCode: true,
        name: `csls: ${this.getFileName(projectPath)}`,
        program: properties.TargetPath,
        request: "launch",
        stopAtEntry: false,
        type: "coreclr",
      },
    );
    if (!started) {
      throw new Error(`The debugger did not start for ${projectPath}.`);
    }
  }

  run(projectPath: string): void {
    const terminal = vscode.window.createTerminal({
      cwd: dirname(projectPath),
      name: `csls: ${this.getFileName(projectPath)}`,
      shellArgs: ["run", "--project", projectPath],
      shellPath: this.dotnetPath,
    });
    terminal.show();
  }

  private async execute(arguments_: readonly string[], cwd: string): Promise<void> {
    const result = await this.executor.execute(arguments_, cwd, undefined, true);
    if (result.exitCode !== 0) {
      const diagnostics = `${result.stdout}\n${result.stderr}`.trim();
      const detail = diagnostics.length === 0
        ? ""
        : `\n${diagnostics.slice(-4_000)}`;
      throw new Error(
        `dotnet ${arguments_[0]} failed with exit code ${result.exitCode ?? "unknown"}.${detail}`,
      );
    }
  }

  private getFileName(projectPath: string): string {
    const separator = Math.max(projectPath.lastIndexOf("/"), projectPath.lastIndexOf("\\"));
    return separator < 0 ? projectPath : projectPath.slice(separator + 1);
  }
}
