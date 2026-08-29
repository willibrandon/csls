import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import test from "node:test";
import { waitForProcessCompletion } from "../../editors/vscode/src/processLifecycle.mts";

test("process completion includes output held open by a descendant", async () => {
  const child = spawn(
    process.execPath,
    [
      "-e",
      `
        const { spawn } = require("node:child_process");
        const descendant = spawn(
          process.execPath,
          ["-e", "setTimeout(() => process.stdout.write('late output'), 250)"],
          { stdio: ["ignore", "inherit", "inherit"] },
        );
        descendant.unref();
      `,
    ],
    { stdio: ["ignore", "pipe", "pipe"] },
  );
  child.stdout.setEncoding("utf8");
  let stdout = "";
  child.stdout.on("data", (value) => {
    stdout += value;
  });

  const exitCode = await waitForProcessCompletion(child);

  assert.equal(exitCode, 0);
  assert.equal(stdout, "late output");
});
