import process from "node:process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runTests } from "@vscode/test-web";

const fixturePath = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(fixturePath, "..", "..");
const port = Number.parseInt(requireEnvironment("CSLS_VSCODE_WEB_PORT"), 10);
if (!Number.isSafeInteger(port) || port < 1 || port > 65_535) {
  throw new Error("CSLS_VSCODE_WEB_PORT must be a TCP port number.");
}

await runTests({
  browserType: requireEnvironment("CSLS_VSCODE_WEB_BROWSER"),
  coi: true,
  extensionDevelopmentPath: resolve(repositoryRoot, "editors", "vscode"),
  extensionTestsPath: resolve(
    repositoryRoot,
    "editors",
    "vscode",
    "dist",
    "test",
    "web-suite.cjs",
  ),
  folderPath: requireEnvironment("CSLS_VSCODE_WORKSPACE_PATH"),
  headless: true,
  port,
  quality: "stable",
  commit: "08d4889f9ec4a1685d257b9b95de036c8e1ce1e5",
  testRunnerDataDir: requireEnvironment("CSLS_VSCODE_WEB_CACHE_PATH"),
});

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
