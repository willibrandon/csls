const assert = require("node:assert/strict");
const vscode = require("vscode");

exports.run = async function run() {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(workspaceFolder, "The VS Code integration workspace must be open.");

  const extension = vscode.extensions.getExtension("willibrandon.csls-vscode-integration");
  assert.ok(extension, "The csls integration extension must be installed.");
  const documentUri = vscode.Uri.joinPath(workspaceFolder.uri, "Program.cs");
  const document = await vscode.workspace.openTextDocument(documentUri);
  await vscode.window.showTextDocument(document);
  assert.equal(document.languageId, "csharp");

  await extension.activate();
  assert.equal(extension.isActive, true);
  const hovers = await vscode.commands.executeCommand(
    "vscode.executeHoverProvider",
    documentUri,
    new vscode.Position(0, 2),
  );
  assert.ok(
    Array.isArray(hovers) && hovers.length > 0,
    "csls must return a hover.",
  );
  const hoverText = hovers
    .flatMap((hover) => hover.contents)
    .map((content) => (typeof content === "string" ? content : content.value))
    .join("\n");
  assert.match(hoverText, /System\.Console/u);
};
