import * as vscode from "vscode";

const startupTimeoutMilliseconds = 120_000;

export async function run(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The VS Code shutdown workspace must be open.");
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The csls extension must be installed.");
  await extension.activate();
  assert(extension.isActive, "The csls extension must be active.");

  const marker = vscode.Uri.joinPath(workspaceFolder.uri, "Tests", "discovery.pid");
  await waitUntil(
    async () => await exists(marker),
    "Automatic test discovery did not start its blocking build.",
  );

  const processId = Number.parseInt(
    new TextDecoder().decode(await vscode.workspace.fs.readFile(marker)).trim(),
    10,
  );
  assert(Number.isSafeInteger(processId), "The discovery process ID was invalid.");
  const generatedDirectory = vscode.Uri.joinPath(
    workspaceFolder.uri,
    "artifacts",
    "obj",
  );
  await vscode.workspace.fs.createDirectory(generatedDirectory);
  await vscode.workspace.fs.writeFile(
    vscode.Uri.joinPath(generatedDirectory, "Generated.g.cs"),
    new TextEncoder().encode("internal sealed class Generated;\n"),
  );
  await new Promise((resolve) => setTimeout(resolve, 2_000));
  assert(
    isProcessRunning(processId),
    "Generated build output restarted automatic test discovery.",
  );
}

function isProcessRunning(processId: number): boolean {
  try {
    process.kill(processId, 0);
    return true;
  } catch {
    return false;
  }
}

async function exists(uri: vscode.Uri): Promise<boolean> {
  try {
    await vscode.workspace.fs.stat(uri);
    return true;
  } catch {
    return false;
  }
}

async function waitUntil(
  condition: () => boolean | Promise<boolean>,
  message: string,
): Promise<void> {
  const deadline = Date.now() + startupTimeoutMilliseconds;
  while (Date.now() < deadline) {
    if (await condition()) {
      return;
    }

    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  throw new Error(message);
}

function assert(condition: boolean, message: string): asserts condition {
  if (!condition) {
    throw new Error(message);
  }
}
