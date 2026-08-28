import * as vscode from "vscode";

export function getDirectoryPath(filePath: string): string {
  const uri = vscode.Uri.file(filePath);
  const slash = uri.path.lastIndexOf("/");
  const parentPath = slash <= 0 ? "/" : uri.path.slice(0, slash);
  return uri.with({ path: parentPath }).fsPath;
}

export function getFileExtension(filePath: string): string {
  const name = getFileName(filePath);
  const dot = name.lastIndexOf(".");
  return dot <= 0 ? "" : name.slice(dot);
}

export function getFileName(filePath: string): string {
  const path = vscode.Uri.file(filePath).path.replace(/\/+$/u, "");
  return path.slice(path.lastIndexOf("/") + 1);
}

export function getRelativeFilePath(
  directoryPath: string,
  filePath: string,
): string | undefined {
  const directoryUri = vscode.Uri.file(directoryPath);
  const fileUri = vscode.Uri.file(filePath);
  if (directoryUri.scheme !== fileUri.scheme || directoryUri.authority !== fileUri.authority) {
    return undefined;
  }

  const directorySegments = splitPath(directoryUri.path);
  const fileSegments = splitPath(fileUri.path);
  if (directorySegments.length >= fileSegments.length) {
    return undefined;
  }

  const ignoreCase = /^[A-Za-z]:[\\/]/u.test(directoryPath) || directoryUri.authority.length > 0;
  for (let index = 0; index < directorySegments.length; index++) {
    const directorySegment = directorySegments[index] ?? "";
    const fileSegment = fileSegments[index] ?? "";
    if (ignoreCase
      ? directorySegment.localeCompare(fileSegment, undefined, { sensitivity: "accent" }) !== 0
      : directorySegment !== fileSegment) {
      return undefined;
    }
  }

  return fileSegments.slice(directorySegments.length).join("/");
}

function splitPath(path: string): string[] {
  return path.split("/").filter((segment) => segment.length > 0);
}
