import * as vscode from "vscode";

type ServerLogLevel = "trace" | "debug" | "info" | "warn" | "error";

export class LanguageServerLogOutputChannel implements vscode.LogOutputChannel {
  private level: ServerLogLevel = "error";

  constructor(private readonly channel: vscode.LogOutputChannel) {}

  get name(): string {
    return this.channel.name;
  }

  get logLevel(): vscode.LogLevel {
    return this.channel.logLevel;
  }

  get onDidChangeLogLevel(): vscode.Event<vscode.LogLevel> {
    return this.channel.onDidChangeLogLevel;
  }

  append(value: string): void {
    this.channel.append(value);
  }

  appendLine(value: string): void {
    this.channel.appendLine(value);
  }

  replace(value: string): void {
    this.channel.replace(value);
  }

  clear(): void {
    this.channel.clear();
  }

  show(preserveFocus?: boolean): void;
  show(column?: vscode.ViewColumn, preserveFocus?: boolean): void;
  show(columnOrPreserveFocus?: vscode.ViewColumn | boolean, preserveFocus?: boolean): void {
    if (typeof columnOrPreserveFocus === "boolean") {
      this.channel.show(columnOrPreserveFocus);
      return;
    }

    this.channel.show(columnOrPreserveFocus, preserveFocus);
  }

  hide(): void {
    this.channel.hide();
  }

  dispose(): void {}

  trace(message: string, ...args: readonly unknown[]): void {
    this.channel.trace(message, ...args);
  }

  debug(message: string, ...args: readonly unknown[]): void {
    this.channel.debug(message, ...args);
  }

  info(message: string, ...args: readonly unknown[]): void {
    this.channel.info(message, ...args);
  }

  warn(message: string, ...args: readonly unknown[]): void {
    this.channel.warn(message, ...args);
  }

  error(error: string | Error, ...args: readonly unknown[]): void {
    if (error instanceof Error) {
      this.level = "error";
      this.channel.error(error, ...args);
      return;
    }

    const match = /^(trce|dbug|info|warn|fail|crit):\s*/u.exec(error);
    let message = error;
    if (match !== null) {
      this.level = toServerLogLevel(match[1] ?? "");
      message = error.slice(match[0].length);
    } else if (!/^\s/u.test(error)) {
      this.level = "error";
    }

    this.write(this.level, message, args);
  }

  private write(
    level: ServerLogLevel,
    message: string,
    args: readonly unknown[],
  ): void {
    switch (level) {
      case "trace":
        this.channel.trace(message, ...args);
        break;
      case "debug":
        this.channel.debug(message, ...args);
        break;
      case "info":
        this.channel.info(message, ...args);
        break;
      case "warn":
        this.channel.warn(message, ...args);
        break;
      case "error":
        this.channel.error(message, ...args);
        break;
    }
  }
}

function toServerLogLevel(prefix: string): ServerLogLevel {
  switch (prefix) {
    case "trce":
      return "trace";
    case "dbug":
      return "debug";
    case "info":
      return "info";
    case "warn":
      return "warn";
    case "fail":
    case "crit":
      return "error";
    default:
      return "error";
  }
}
