import process from "node:process";
import { downloadAndUnzipVSCode, SilentReporter } from "@vscode/test-electron";
import { open } from "@vscode/test-web";

if (process.argv[2] === "--web-only") {
  if (process.argv.length !== 4) {
    throw new Error(
      "Usage: node tests/vscode/provision.mjs --web-only <web-cache-directory>",
    );
  }

  const server = await open({
    browserType: "none",
    port: 0,
    quality: "stable",
    testRunnerDataDir: process.argv[3],
  });
  server.dispose();
  process.stdout.write(process.argv[3] + "\n");
  process.exit(0);
}

if (process.argv.length < 3 || process.argv.length > 4) {
  throw new Error(
    "Usage: node tests/vscode/provision.mjs <desktop-cache-directory> " +
      "[web-cache-directory] | --web-only <web-cache-directory>",
  );
}

const executablePath = await downloadAndUnzipVSCode({
  cachePath: process.argv[2],
  reporter: new SilentReporter(),
  timeout: 120_000,
  version: "stable",
});
if (process.argv.length === 4) {
  const server = await open({
    browserType: "none",
    port: 0,
    quality: "stable",
    testRunnerDataDir: process.argv[3],
  });
  server.dispose();
}

process.stdout.write(executablePath + "\n");
