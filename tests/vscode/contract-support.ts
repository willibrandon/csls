import * as vscode from "vscode";

export const languageFeatureTimeoutMilliseconds = 30_000;

const progress = vscode.window.createOutputChannel("csls Integration Tests", { log: true });

export async function step<T>(name: string, operation: () => Thenable<T>): Promise<T> {
  const started = Date.now();
  progress.info(`Starting: ${name}`);
  try {
    const result = await operation();
    progress.info(`Completed: ${name} (${Date.now() - started} ms)`);
    return result;
  } catch (error) {
    progress.error(`Failed: ${name}`, error instanceof Error ? error : String(error));
    throw error;
  }
}

export function assert(condition: boolean, message: string): asserts condition {
  if (!condition) {
    throw new Error(message);
  }
}

export function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

export async function waitUntil(
  condition: () => boolean | Promise<boolean>,
  message: string,
): Promise<void> {
  const deadline = Date.now() + languageFeatureTimeoutMilliseconds;
  while (Date.now() < deadline) {
    if (await condition()) {
      return;
    }

    await delay(100);
  }

  throw new Error(message);
}

export async function withTimeout<T>(
  operation: Thenable<T>,
  milliseconds: number,
  message: string,
): Promise<T> {
  let timeout: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      operation,
      new Promise<T>((_resolve, reject) => {
        timeout = setTimeout(() => reject(new Error(message)), milliseconds);
      }),
    ]);
  } finally {
    if (timeout !== undefined) {
      clearTimeout(timeout);
    }
  }
}
