import * as vscode from "vscode";

const peekReferencesCommand = "csls.client.peekReferences";

interface ProtocolPosition {
  readonly character: number;
  readonly line: number;
}

export function registerReferenceCodeLensCommands(
  context: vscode.ExtensionContext,
  toCodeUri: (value: string) => vscode.Uri,
): void {
  context.subscriptions.push(vscode.commands.registerCommand(
    peekReferencesCommand,
    async (uriValue: string, protocolPosition: ProtocolPosition): Promise<void> => {
      const uri = toCodeUri(uriValue);
      const position = new vscode.Position(
        protocolPosition.line,
        protocolPosition.character,
      );
      const references = await vscode.commands.executeCommand<readonly vscode.Location[]>(
        "vscode.executeReferenceProvider",
        uri,
        position,
      );
      if (Array.isArray(references)) {
        await vscode.commands.executeCommand(
          "editor.action.showReferences",
          uri,
          position,
          references,
        );
      }
    },
  ));
}
