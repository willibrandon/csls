import * as vscode from "vscode";

const virtualWorkspaceRoot = vscode.Uri.file("/workspace");

export class BrowserWorkspaceMapping {
  private readonly entries: Array<{
    readonly source: vscode.WorkspaceFolder;
    readonly virtualUri: vscode.Uri;
  }> = [];
  private nextVirtualFolderId = 0;

  constructor(folders: readonly vscode.WorkspaceFolder[]) {
    this.addFolders(folders);
  }

  get folders(): readonly vscode.WorkspaceFolder[] {
    return this.entries.map((entry) => entry.source);
  }

  get virtualFolders(): readonly vscode.WorkspaceFolder[] {
    return this.entries.map((entry) => ({
      index: entry.source.index,
      name: entry.source.name,
      uri: entry.virtualUri,
    }));
  }

  addFolders(folders: readonly vscode.WorkspaceFolder[]): void {
    for (const folder of folders) {
      if (this.findEntry(folder.uri) !== undefined) {
        continue;
      }

      this.entries.push({
        source: folder,
        virtualUri: vscode.Uri.joinPath(
          virtualWorkspaceRoot,
          (this.nextVirtualFolderId++).toString(),
        ),
      });
    }
  }

  removeFolders(folders: readonly vscode.WorkspaceFolder[]): void {
    const removedUris = new Set(folders.map((folder) => folder.uri.toString()));
    for (let index = this.entries.length - 1; index >= 0; index--) {
      const entry = this.entries[index];
      if (entry !== undefined && removedUris.has(entry.source.uri.toString())) {
        this.entries.splice(index, 1);
      }
    }
  }

  getVirtualFolderPath(folder: vscode.WorkspaceFolder): string {
    const entry = this.findEntry(folder.uri);
    if (entry === undefined) {
      throw new Error(`The workspace folder is not mapped: ${folder.uri.toString()}`);
    }

    return entry.virtualUri.fsPath;
  }

  toProtocolUri = (uri: vscode.Uri): string => {
    for (const entry of this.entries) {
      const relativePath = getRelativeUriPath(entry.source.uri, uri);
      if (relativePath !== undefined) {
        return vscode.Uri.joinPath(entry.virtualUri, relativePath).toString();
      }
    }

    return uri.toString();
  };

  toCodeUri = (value: string): vscode.Uri => {
    const uri = vscode.Uri.parse(value);
    for (const entry of this.entries) {
      const relativePath = getRelativeUriPath(entry.virtualUri, uri);
      if (relativePath !== undefined) {
        return vscode.Uri.joinPath(entry.source.uri, relativePath);
      }
    }

    return uri;
  };

  toVirtualPath(uri: vscode.Uri): string {
    const protocolUri = vscode.Uri.parse(this.toProtocolUri(uri));
    if (protocolUri.scheme !== "file") {
      throw new Error(`The workspace URI could not be mapped into the browser filesystem: ${uri.toString()}`);
    }

    return protocolUri.fsPath;
  }

  private findEntry(uri: vscode.Uri): {
    readonly source: vscode.WorkspaceFolder;
    readonly virtualUri: vscode.Uri;
  } | undefined {
    const value = uri.toString();
    return this.entries.find((entry) => entry.source.uri.toString() === value);
  }
}

function getRelativeUriPath(root: vscode.Uri, candidate: vscode.Uri): string | undefined {
  if (root.scheme !== candidate.scheme || root.authority !== candidate.authority) {
    return undefined;
  }

  const rootPath = root.path.endsWith("/") ? root.path : `${root.path}/`;
  if (candidate.path === root.path) {
    return "";
  }

  return candidate.path.startsWith(rootPath)
    ? candidate.path.slice(rootPath.length)
    : undefined;
}
