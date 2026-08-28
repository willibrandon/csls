import type {
  InitializeParams,
  LanguageClientOptions,
} from "vscode-languageclient/browser";
import {
  BrowserMessageReader,
  BrowserMessageWriter,
  LanguageClient,
} from "vscode-languageclient/browser";
import type { BrowserWorkspaceMapping } from "./browserWorkspaceMapping.js";

export class BrowserLanguageClient extends LanguageClient {
  readonly mapping: BrowserWorkspaceMapping;

  constructor(
    port: MessagePort,
    clientOptions: LanguageClientOptions,
    mapping: BrowserWorkspaceMapping,
  ) {
    super(
      "csls",
      "csls",
      async () => ({
        reader: new BrowserMessageReader(port),
        writer: new BrowserMessageWriter(port),
      }),
      clientOptions,
    );
    this.mapping = mapping;
  }

  protected override fillInitializeParams(parameters: InitializeParams): void {
    super.fillInitializeParams(parameters);
    const firstFolder = this.mapping.virtualFolders[0];
    parameters.rootPath = firstFolder?.uri.fsPath ?? null;
    parameters.rootUri = firstFolder?.uri.toString() ?? null;
    parameters.workspaceFolders = this.mapping.virtualFolders.map((folder) => ({
      name: folder.name,
      uri: folder.uri.toString(),
    }));
  }
}
