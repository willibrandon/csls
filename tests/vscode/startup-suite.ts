import * as vscode from "vscode";

const workspaceLoadTimeoutMilliseconds = 240_000;

export async function run(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The csls repository workspace must be open.");
  await vscode.workspace.fs.stat(vscode.Uri.joinPath(workspaceFolder.uri, "Csls.slnx"));

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
    return observedProjects.some((project) => project.name === "Csls.App") &&
      observedProjects.some((project) => project.name === "Generate-Docs.cs");
  }, () =>
    "The real csls workspace did not load its solution and file-based apps. " +
    `Received ${JSON.stringify(observedProjects)}.`);

  await vscode.commands.executeCommand("csls.refreshSolution");
  const projects = api.projects();
  assert(
    projects.some((project) => project.name === "Csls.App"),
    `The real solution must contain Csls.App. Received ${JSON.stringify(projects)}.`,
  );
  assert(
    projects.some((project) => project.name === "Generate-Docs.cs"),
    `The real workspace must contain Generate-Docs.cs. Received ${JSON.stringify(projects)}.`,
  );
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
