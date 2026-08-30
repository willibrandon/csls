import process from "node:process";
import { downloadAndUnzipVSCode, SilentReporter } from "@vscode/test-electron";

if (process.argv.length !== 3) {
  throw new Error("Usage: node tests/vscode/provision.mjs <cache-directory>");
}

const executablePath = await downloadAndUnzipVSCode({
  cachePath: process.argv[2],
  reporter: new SilentReporter(),
  timeout: 120_000,
  version: "stable",
});
process.stdout.write(executablePath + "\n");
