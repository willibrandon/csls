import * as vscode from "vscode";
import {
  getDirectoryPath,
  getFileExtension,
  getFileName,
  getRelativeFilePath,
} from "./filePath.js";
import { SolutionTreeItem } from "./solutionTreeItem.js";
import type { WorkspaceInfo } from "./workspaceInfo.js";
import type { WorkspaceInfoClient } from "./workspaceInfoClient.js";

export class SolutionTreeProvider implements vscode.TreeDataProvider<SolutionTreeItem>, vscode.Disposable {
  private readonly changed = new vscode.EventEmitter<SolutionTreeItem | undefined>();
  private client: WorkspaceInfoClient | undefined;
  private roots: SolutionTreeItem[] = [];

  constructor(
    private readonly outputChannel: vscode.LogOutputChannel,
    private readonly mapPath: (path: string) => vscode.Uri = vscode.Uri.file,
  ) {}

  readonly onDidChangeTreeData = this.changed.event;

  getTreeItem(element: SolutionTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: SolutionTreeItem): SolutionTreeItem[] {
    return [...(element?.children ?? this.roots)];
  }

  async attach(client: WorkspaceInfoClient): Promise<void> {
    this.client = client;
    await this.refresh();
  }

  async refresh(): Promise<void> {
    const client = this.client;
    if (client === undefined) {
      this.roots = [];
      this.changed.fire(undefined);
      return;
    }

    const snapshot = await client.sendRequest<WorkspaceInfo>("$/csharp/workspaceInfo");
    this.roots = this.createRoots(snapshot);
    this.changed.fire(undefined);
  }

  async restore(): Promise<void> {
    const client = this.client;
    if (client === undefined) {
      throw new Error("The csls language client is not connected.");
    }

    this.outputChannel.show(true);
    this.outputChannel.info("Restoring the loaded workspace.");
    const result = await client.sendRequest<{
      readonly currentGeneration: number;
    }>("$/csharp/workspace/restore");
    this.outputChannel.info(
      `Restored the workspace at generation ${result.currentGeneration}.`,
    );
    await this.refresh();
  }

  getProjectItems(): readonly SolutionTreeItem[] {
    const projects: SolutionTreeItem[] = [];
    const visit = (item: SolutionTreeItem) => {
      if (item.contextValue === "cslsProject") {
        projects.push(item);
      }

      for (const child of item.children) {
        visit(child);
      }
    };
    for (const root of this.roots) {
      visit(root);
    }

    return projects;
  }

  dispose(): void {
    this.client = undefined;
    this.changed.dispose();
  }

  private createRoots(snapshot: WorkspaceInfo): SolutionTreeItem[] {
    const projectsById = new Map(snapshot.projects.map((project) => [project.id, project]));
    return snapshot.workspaces.map((workspace) => {
      const label = this.getWorkspaceLabel(workspace.rootPath);
      const root = new SolutionTreeItem(
        label,
        "cslsSolution",
        vscode.TreeItemCollapsibleState.Expanded,
      );
      root.description = `${workspace.projectCount} projects`;
      root.iconPath = new vscode.ThemeIcon("symbol-namespace");
      root.resourceUri = this.mapPath(workspace.rootPath);
      root.workspaceRoot = workspace.rootPath;
      const projects = snapshot.projects
        .filter((project) => project.workspaceRoot === workspace.rootPath)
        .sort((left, right) => left.name.localeCompare(right.name));
      for (const project of projects) {
        root.children.push(this.createProject(project, snapshot, projectsById));
      }

      return root;
    });
  }

  private createProject(
    project: WorkspaceInfo["projects"][number],
    snapshot: WorkspaceInfo,
    projectsById: ReadonlyMap<string, WorkspaceInfo["projects"][number]>,
  ): SolutionTreeItem {
    const item = new SolutionTreeItem(
      project.name,
      "cslsProject",
      vscode.TreeItemCollapsibleState.Collapsed,
    );
    item.description = project.filePath === undefined
      ? project.language
      : getFileExtension(project.filePath);
    item.iconPath = new vscode.ThemeIcon("project");
    item.projectPath = project.filePath;
    item.workspaceRoot = project.workspaceRoot;
    if (project.filePath !== undefined) {
      item.resourceUri = this.mapPath(project.filePath);
      item.tooltip = project.filePath;
      item.command = {
        command: "vscode.open",
        title: "Open Project",
        arguments: [item.resourceUri],
      };
    }

    const dependencies = this.createDependencies(project, projectsById);
    if (dependencies.children.length > 0) {
      item.children.push(dependencies);
    }

    const documents = snapshot.documents.filter((document) => document.projectId === project.id);
    item.children.push(...this.createDocumentTree(project, documents));
    return item;
  }

  private createDependencies(
    project: WorkspaceInfo["projects"][number],
    projectsById: ReadonlyMap<string, WorkspaceInfo["projects"][number]>,
  ): SolutionTreeItem {
    const dependencies = new SolutionTreeItem(
      "Dependencies",
      "cslsDependencies",
      vscode.TreeItemCollapsibleState.Collapsed,
    );
    dependencies.iconPath = new vscode.ThemeIcon("references");
    for (const referenceId of project.projectReferenceIds) {
      const reference = projectsById.get(referenceId);
      const item = new SolutionTreeItem(
        reference?.name ?? referenceId,
        "cslsProjectReference",
      );
      item.iconPath = new vscode.ThemeIcon("symbol-class");
      item.description = "project";
      item.projectPath = reference?.filePath;
      dependencies.children.push(item);
    }

    for (const analyzerPath of project.analyzerPaths) {
      const item = new SolutionTreeItem(getFileName(analyzerPath), "cslsAnalyzer");
      item.description = "analyzer";
      item.iconPath = new vscode.ThemeIcon("symbol-event");
      item.resourceUri = this.mapPath(analyzerPath);
      item.tooltip = analyzerPath;
      dependencies.children.push(item);
    }

    dependencies.children.sort((left, right) =>
      String(left.label).localeCompare(String(right.label)));
    return dependencies;
  }

  private createDocumentTree(
    project: WorkspaceInfo["projects"][number],
    documents: readonly WorkspaceInfo["documents"][number][],
  ): SolutionTreeItem[] {
    const roots: SolutionTreeItem[] = [];
    const folders = new Map<string, SolutionTreeItem>();
    const projectDirectory = project.filePath === undefined
      ? project.workspaceRoot
      : getDirectoryPath(project.filePath);
    for (const document of documents) {
      const filePath = document.filePath;
      if (filePath === undefined) {
        continue;
      }

      const relativePath = getRelativeFilePath(projectDirectory, filePath) ??
        getFileName(filePath);
      const segments = relativePath.split("/").filter((segment) => segment.length > 0);
      let children = roots;
      let folderPath = "";
      for (const segment of segments.slice(0, -1)) {
        folderPath = folderPath.length === 0 ? segment : `${folderPath}/${segment}`;
        let folder = folders.get(folderPath);
        if (folder === undefined) {
          folder = new SolutionTreeItem(
            segment,
            "cslsFolder",
            vscode.TreeItemCollapsibleState.Collapsed,
          );
          folder.iconPath = new vscode.ThemeIcon("folder");
          folders.set(folderPath, folder);
          children.push(folder);
        }

        children = folder.children;
      }

      const file = new SolutionTreeItem(document.name, "cslsDocument");
      file.iconPath = new vscode.ThemeIcon("file-code");
      file.resourceUri = this.mapPath(filePath);
      file.tooltip = filePath;
      file.command = {
        command: "vscode.open",
        title: "Open Document",
        arguments: [file.resourceUri],
      };
      children.push(file);
    }

    this.sortDocuments(roots);
    return roots;
  }

  private sortDocuments(items: SolutionTreeItem[]): void {
    items.sort((left, right) => {
      const leftFolder = left.contextValue === "cslsFolder";
      const rightFolder = right.contextValue === "cslsFolder";
      if (leftFolder !== rightFolder) {
        return leftFolder ? -1 : 1;
      }

      return String(left.label).localeCompare(String(right.label));
    });
    for (const item of items) {
      this.sortDocuments(item.children);
    }
  }

  private getWorkspaceLabel(rootPath: string): string {
    const extension = getFileExtension(rootPath).toLowerCase();
    return extension === ".sln" || extension === ".slnx" || extension === ".csproj"
      ? getFileName(rootPath)
      : getFileName(rootPath) || rootPath;
  }
}
