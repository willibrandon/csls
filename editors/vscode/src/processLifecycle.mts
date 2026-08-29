import type { ChildProcess } from "node:child_process";

export function waitForProcessCompletion(
  childProcess: ChildProcess,
): Promise<number | null> {
  return new Promise((resolve, reject) => {
    childProcess.once("error", reject);
    childProcess.once("close", resolve);
  });
}
