import * as vscode from "vscode";

const workspaceLoadTimeoutMilliseconds = 240_000;

export async function run(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The csls repository workspace must be open.");
  await vscode.workspace.fs.stat(vscode.Uri.joinPath(workspaceFolder.uri, "Csls.slnx"));
  const unexpectedDiagnostics = new Set<string>();
  const recordDiagnostics = (uris: readonly vscode.Uri[]): void => {
    for (const uri of uris) {
      if (!isCSharpWorkspaceDocument(uri, workspaceFolder)) {
        continue;
      }

      for (const diagnostic of vscode.languages.getDiagnostics(uri)) {
        unexpectedDiagnostics.add(
          `${uri.fsPath}:${diagnostic.range.start.line + 1}: ` +
            `${diagnosticSeverityName(diagnostic.severity)} ${diagnostic.message}`,
        );
      }
    }
  };
  recordDiagnostics(vscode.languages.getDiagnostics().map(([uri]) => uri));
  const diagnosticSubscription = vscode.languages.onDidChangeDiagnostics((event) => {
    recordDiagnostics(event.uris);
  });

  try {
    const extension = vscode.extensions.getExtension("willibrandon.csls");
    assert(extension !== undefined, "The packaged csls extension must be installed.");
    const api: unknown = await extension.activate();
    assert(extension.isActive, "The packaged csls extension must be active.");
    assert(isExtensionApi(api), "The packaged csls extension must return its host API.");
    assert(api.host === "remote", `Expected the remote extension host, received ${api.host}.`);
    assert(api.state === 2, "The csls language client must be running.");

    let observedProjects = api.projects();
    await waitUntil(() => {
      observedProjects = api.projects();
      return observedProjects.some((project) => project.name === "Csls.App");
    }, () =>
      "The real csls workspace did not load its solution. " +
      `Received ${JSON.stringify(observedProjects)}.`);
    assert(
      observedProjects.some((project) => project.name === "Generate-Docs.cs"),
      "The real csls workspace did not eagerly load file-based apps during solution startup.",
    );

    await vscode.commands.executeCommand("csls.refreshSolution");
    const projects = api.projects();
    assert(
      projects.some((project) => project.name === "Csls.App"),
      `The real solution must contain Csls.App. Received ${JSON.stringify(projects)}.`,
    );
    const generateDocsUri = vscode.Uri.joinPath(
      workspaceFolder.uri,
      "scripts",
      "Generate-Docs.cs",
    );
    await vscode.workspace.openTextDocument(generateDocsUri);
    await vscode.commands.executeCommand(
      "vscode.executeDocumentSymbolProvider",
      generateDocsUri,
    );
    await vscode.commands.executeCommand("csls.refreshSolution");
    await waitUntil(() => {
      observedProjects = api.projects();
      return observedProjects.some((project) => project.name === "Generate-Docs.cs");
    }, () =>
      "The real csls workspace did not retain opened file-based apps after refresh. " +
      `Received ${JSON.stringify(observedProjects)}.`);
    await waitForDiagnosticQuietPeriod();
    recordDiagnostics([generateDocsUri]);
  } finally {
    diagnosticSubscription.dispose();
  }

  assert(
    unexpectedDiagnostics.size === 0,
    "The csls repository emitted unexpected C# diagnostics during startup:\n" +
      [...unexpectedDiagnostics].sort().join("\n"),
  );
}

function isCSharpWorkspaceDocument(
  uri: vscode.Uri,
  workspaceFolder: vscode.WorkspaceFolder,
): boolean {
  if (uri.scheme !== "file" || vscode.workspace.getWorkspaceFolder(uri) !== workspaceFolder) {
    return false;
  }

  const path = uri.path.toLowerCase();
  return path.endsWith(".cs") ||
    path.endsWith(".csx") ||
    path.endsWith(".cshtml") ||
    path.endsWith(".razor");
}

function diagnosticSeverityName(severity: vscode.DiagnosticSeverity): string {
  switch (severity) {
    case vscode.DiagnosticSeverity.Error:
      return "error";
    case vscode.DiagnosticSeverity.Warning:
      return "warning";
    case vscode.DiagnosticSeverity.Information:
      return "information";
    case vscode.DiagnosticSeverity.Hint:
      return "hint";
  }
}

async function waitForDiagnosticQuietPeriod(): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, 2_000));
}

function isExtensionApi(value: unknown): value is {
  readonly host: string;
  readonly projects: () => readonly {
    readonly name: string;
    readonly path: string;
  }[];
  readonly state: number;
} {
  return typeof value === "object" &&
    value !== null &&
    "host" in value &&
    typeof value.host === "string" &&
    "projects" in value &&
    typeof value.projects === "function" &&
    "state" in value &&
    typeof value.state === "number";
}

async function waitUntil(
  condition: () => boolean | Promise<boolean>,
  message: string | (() => string),
): Promise<void> {
  const deadline = Date.now() + workspaceLoadTimeoutMilliseconds;
  while (Date.now() < deadline) {
    if (await condition()) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  throw new Error(typeof message === "string" ? message : message());
}

function assert(condition: boolean, message: string): asserts condition {
  if (!condition) {
    throw new Error(message);
  }
}
