import * as vscode from "vscode";
import type { BrowserWorkspaceMapping } from "./browserWorkspaceMapping.js";
import { SolutionTreeProvider } from "./solutionTreeProvider.js";
import type { WorkspaceInfoClient } from "./workspaceInfoClient.js";

export class BrowserWorkspaceExperience implements vscode.Disposable {
  private readonly disposables: vscode.Disposable[];
  private readonly provider: SolutionTreeProvider;
  private disposed = false;

  constructor(
    outputChannel: vscode.LogOutputChannel,
    mapping: BrowserWorkspaceMapping,
  ) {
    this.provider = new SolutionTreeProvider(
      outputChannel,
      (path) => mapping.toCodeUri(vscode.Uri.file(path).toString()),
    );
    this.disposables = [
      this.provider,
      vscode.window.createTreeView("csls.solution", {
        showCollapseAll: true,
        treeDataProvider: this.provider,
      }),
      vscode.commands.registerCommand("csls.refreshSolution", () => this.run(
        () => this.provider.refresh(),
      )),
      vscode.commands.registerCommand("csls.restoreSolution", () => this.run(
        () => this.provider.restore(),
      )),
    ];
  }

  async attach(client: WorkspaceInfoClient): Promise<void> {
    await this.provider.attach(client);
  }

  getProjects(): readonly {
    readonly name: string;
    readonly path: string;
  }[] {
    return this.provider
      .getProjectItems()
      .filter((project) => project.projectPath !== undefined)
      .map((project) => ({
        name: String(project.label),
        path: project.projectPath ?? "",
      }));
  }

  dispose(): void {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    for (const disposable of this.disposables.reverse()) {
      disposable.dispose();
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    try {
      await action();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      void vscode.window.showErrorMessage(message);
      throw error;
    }
  }
}
