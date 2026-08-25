const vscode = require("vscode");
const { LanguageClient } = require("vscode-languageclient/node");

let client;

exports.activate = async function activate(context) {
  const launcherPath = requireEnvironment("CSLS_VSCODE_LAUNCHER_PATH");
  const workerPath = requireEnvironment("CSLS_VSCODE_WORKER_PATH");
  const dotnetPath = requireEnvironment("CSLS_VSCODE_DOTNET_PATH");
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  if (workspaceFolder === undefined) {
    throw new Error("The VS Code integration workspace is not open.");
  }

  const outputChannel = vscode.window.createOutputChannel("csls integration", { log: true });
  client = new LanguageClient(
    "csls-integration",
    "csls integration",
    {
      command: dotnetPath,
      args: [launcherPath, "lsp"],
      options: {
        cwd: workspaceFolder.uri.fsPath,
        env: { ...process.env, CSLS_WORKER_PATH: workerPath },
      },
    },
    {
      documentSelector: [{ language: "csharp", scheme: "file" }],
      outputChannel,
      workspaceFolder,
    },
  );
  context.subscriptions.push(outputChannel, client);
  await client.start();
};

exports.deactivate = async function deactivate() {
  if (client !== undefined) {
    await client.stop();
    client = undefined;
  }
};

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
