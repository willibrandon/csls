import { access, mkdir } from "node:fs/promises";
import { isAbsolute, join } from "node:path";
import * as vscode from "vscode";
import { ProcessExecutor } from "./processExecutor.js";

export class DebuggerProvider implements vscode.DebugAdapterDescriptorFactory, vscode.Disposable {
  private readonly disposables: vscode.Disposable[];
  private readonly installer: ProcessExecutor;
  private readonly installerArguments: readonly string[];
  private debuggerPathPromise: Promise<string> | undefined;

  constructor(
    context: vscode.ExtensionContext,
    serverPath: string,
    runtimePath: string,
    outputChannel: vscode.LogOutputChannel,
  ) {
    const managedLauncher = serverPath.endsWith(".dll");
    this.installer = new ProcessExecutor(
      managedLauncher ? runtimePath : serverPath,
      outputChannel,
      "csls",
    );
    this.installerArguments = managedLauncher ? [serverPath] : [];
    this.disposables = [
      vscode.debug.registerDebugAdapterDescriptorFactory("coreclr", this),
    ];
    this.storagePath = join(context.globalStorageUri.fsPath, "debugger");
  }

  private readonly storagePath: string;

  async createDebugAdapterDescriptor(): Promise<vscode.DebugAdapterDescriptor> {
    return new vscode.DebugAdapterExecutable(
      await this.resolveDebuggerPath(),
      ["--interpreter=vscode"],
    );
  }

  dispose(): void {
    for (const disposable of this.disposables.reverse()) {
      disposable.dispose();
    }
  }

  private async resolveDebuggerPath(): Promise<string> {
    const configuredPath = vscode.workspace
      .getConfiguration("csls.debugger")
      .get<string>("path", "")
      .trim();
    if (configuredPath.length > 0) {
      if (!isAbsolute(configuredPath)) {
        throw new Error("csls.debugger.path must be absolute when it is configured.");
      }

      await access(configuredPath);
      return configuredPath;
    }

    this.debuggerPathPromise ??= this.installDebugger();
    return this.debuggerPathPromise;
  }

  private async installDebugger(): Promise<string> {
    await mkdir(this.storagePath, { recursive: true });
    const result = await this.installer.execute(
      [
        ...this.installerArguments,
        "debugger",
        "install",
        "--output",
        this.storagePath,
      ],
      this.storagePath,
      undefined,
      true,
    );
    if (result.exitCode !== 0) {
      const diagnostics = `${result.stdout}\n${result.stderr}`.trim();
      throw new Error(
        `csls could not install the .NET debugger.${diagnostics.length === 0 ? "" : `\n${diagnostics}`}`,
      );
    }

    const debuggerPath = result.stdout
      .split(/\r?\n/gu)
      .map((line) => line.trim())
      .findLast((line) => line.length > 0);
    if (debuggerPath === undefined || !isAbsolute(debuggerPath)) {
      throw new Error("csls did not return an absolute .NET debugger path.");
    }

    await access(debuggerPath);
    return debuggerPath;
  }
}
