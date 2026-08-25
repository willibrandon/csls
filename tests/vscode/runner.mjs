import process from "node:process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runTests } from "@vscode/test-electron";

const extensionPath = dirname(fileURLToPath(import.meta.url));
const workspacePath = requireEnvironment("CSLS_VSCODE_WORKSPACE_PATH");
const userDataPath = requireEnvironment("CSLS_VSCODE_USER_DATA_PATH");
const extensionsPath = requireEnvironment("CSLS_VSCODE_EXTENSIONS_PATH");

await runTests({
  cachePath: requireEnvironment("CSLS_VSCODE_CACHE_PATH"),
  extensionDevelopmentPath: extensionPath,
  extensionTestsEnv: {
    CSLS_VSCODE_DOTNET_PATH: requireEnvironment("CSLS_VSCODE_DOTNET_PATH"),
    CSLS_VSCODE_LAUNCHER_PATH: requireEnvironment("CSLS_VSCODE_LAUNCHER_PATH"),
    CSLS_VSCODE_WORKER_PATH: requireEnvironment("CSLS_VSCODE_WORKER_PATH"),
  },
  extensionTestsPath: resolve(extensionPath, "suite.cjs"),
  launchArgs: [
    workspacePath,
    "--disable-extensions",
    "--disable-gpu",
    "--disable-telemetry",
    "--disable-updates",
    "--disable-workspace-trust",
    "--extensions-dir=" + extensionsPath,
    "--skip-release-notes",
    "--skip-welcome",
    "--user-data-dir=" + userDataPath,
  ],
  version: "1.134.0",
});

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
