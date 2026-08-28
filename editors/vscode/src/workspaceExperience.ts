import type { LanguageClient } from "vscode-languageclient/node";
import * as vscode from "vscode";
import { DotnetCommandRunner } from "./dotnetCommandRunner.js";
import { SolutionTreeItem } from "./solutionTreeItem.js";
import { SolutionTreeProvider } from "./solutionTreeProvider.js";
import { TestExplorer } from "./testExplorer.js";

export class WorkspaceExperience implements vscode.Disposable {
  private readonly provider: SolutionTreeProvider;
  private readonly runner: DotnetCommandRunner;
  private readonly tests: TestExplorer;
  private readonly disposables: vscode.Disposable[];
  private disposed = false;

  constructor(
    sdkPath: string,
    outputChannel: vscode.LogOutputChannel,
  ) {
    this.provider = new SolutionTreeProvider(outputChannel);
    this.runner = new DotnetCommandRunner(sdkPath, outputChannel);
    this.tests = new TestExplorer(sdkPath, outputChannel, () => this.getProjects());
    this.disposables = [
      this.provider,
      this.tests,
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
      vscode.commands.registerCommand("csls.build", (item?: SolutionTreeItem) => this.run(
        async () => this.runner.build(await this.selectTarget(item)),
      )),
      vscode.commands.registerCommand("csls.restore", (item?: SolutionTreeItem) => this.run(
        async () => {
          await this.runner.restore(await this.selectTarget(item));
          await this.provider.refresh();
        },
      )),
      vscode.commands.registerCommand("csls.run", (item?: SolutionTreeItem) => this.run(
        async () => this.runner.run(await this.selectProject(item)),
      )),
      vscode.commands.registerCommand("csls.debug", (item?: SolutionTreeItem) => this.run(
        async () => this.runner.debug(await this.selectProject(item)),
      )),
      vscode.commands.registerCommand("csls.test", (item?: SolutionTreeItem) => this.run(
        async () => this.tests.run(item?.projectPath),
      )),
    ];
  }

  async attach(client: LanguageClient): Promise<void> {
    await this.provider.attach(client);
    this.tests.refreshInBackground();
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

  getTestNames(): readonly string[] {
    return this.tests.getTestNames();
  }

  getTestErrors(): readonly string[] {
    return this.tests.getErrors();
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

  private async selectTarget(item?: SolutionTreeItem): Promise<string> {
    if (item?.projectPath !== undefined) {
      return item.projectPath;
    }

    if (item?.workspaceRoot !== undefined) {
      return item.workspaceRoot;
    }

    return this.selectProject(item);
  }

  private async selectProject(item?: SolutionTreeItem): Promise<string> {
    if (item?.projectPath !== undefined) {
      return item.projectPath;
    }

    const projects = this.provider
      .getProjectItems()
      .filter((project) => project.projectPath !== undefined);
    const selected = await vscode.window.showQuickPick<{
      readonly description: string;
      readonly label: string;
      readonly project: SolutionTreeItem;
    }>(
      projects.map((project) => ({
        description: project.projectPath ?? "",
        label: String(project.label),
        project,
      })),
      { placeHolder: "Select a project" },
    );
    if (selected?.project.projectPath === undefined) {
      throw new Error("No project was selected.");
    }

    return selected.project.projectPath;
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
