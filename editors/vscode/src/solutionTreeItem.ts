import * as vscode from "vscode";

export class SolutionTreeItem extends vscode.TreeItem {
  readonly children: SolutionTreeItem[] = [];
  projectPath: string | undefined;
  workspaceRoot: string | undefined;

  constructor(
    label: string,
    contextValue: string,
    collapsibleState = vscode.TreeItemCollapsibleState.None,
  ) {
    super(label, collapsibleState);
    this.contextValue = contextValue;
  }
}
