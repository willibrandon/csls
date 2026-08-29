import type {
  InitializeParams,
  LanguageClientOptions,
  ServerOptions,
} from "vscode-languageclient/node";
import { LanguageClient } from "vscode-languageclient/node";

export class DesktopLanguageClient extends LanguageClient {
  constructor(serverOptions: ServerOptions, clientOptions: LanguageClientOptions) {
    super("csls", "csls", serverOptions, clientOptions);
  }

  protected override fillInitializeParams(parameters: InitializeParams): void {
    super.fillInitializeParams(parameters);
    const experimental = asRecord(parameters.capabilities.experimental);
    parameters.capabilities.experimental = {
      ...experimental,
      csharp: {
        ...asRecord(experimental.csharp),
        metadataUris: true,
      },
    };
  }
}

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {};
}
