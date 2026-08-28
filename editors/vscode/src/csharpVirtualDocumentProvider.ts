import * as vscode from "vscode";
import type { BaseLanguageClient } from "vscode-languageclient";

const csharpScheme = "csharp";

export function registerCSharpVirtualDocumentProvider(
  context: vscode.ExtensionContext,
  getClient: () => BaseLanguageClient | undefined,
): void {
  context.subscriptions.push(
    vscode.workspace.registerTextDocumentContentProvider(csharpScheme, {
      async provideTextDocumentContent(
        uri: vscode.Uri,
        cancellationToken: vscode.CancellationToken,
      ): Promise<string> {
        const languageClient = getClient();
        if (languageClient === undefined) {
          throw new Error("The csls language client is not connected.");
        }

        const response = await languageClient.sendRequest<{
          readonly source: string;
        } | null>(
          "csharp/metadata",
          { textDocument: { uri: uri.toString() } },
          cancellationToken,
        );
        if (response === null) {
          throw new Error(`csls could not resolve ${uri.toString()}.`);
        }

        return response.source;
      },
    }),
  );
}
