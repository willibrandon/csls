import * as vscode from "vscode";

export class DebuggerProvider implements vscode.DebugAdapterDescriptorFactory, vscode.Disposable {
  private readonly arguments_: readonly string[];
  private readonly command: string;
  private readonly registration: vscode.Disposable;

  constructor(serverPath: string, runtimePath: string) {
    const managedLauncher = serverPath.endsWith(".dll");
    this.command = managedLauncher ? runtimePath : serverPath;
    this.arguments_ = managedLauncher
      ? [serverPath, "debugger", "dap"]
      : ["debugger", "dap"];
    this.registration = vscode.debug.registerDebugAdapterDescriptorFactory("coreclr", this);
  }

  createDebugAdapterDescriptor(): vscode.DebugAdapterDescriptor {
    return new vscode.DebugAdapterExecutable(this.command, [...this.arguments_]);
  }

  dispose(): void {
    this.registration.dispose();
  }
}
