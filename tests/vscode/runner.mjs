import process from "node:process";
import { spawnSync } from "node:child_process";
import { rename, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { Writable } from "node:stream";
import { pathToFileURL } from "node:url";
import { fileURLToPath } from "node:url";
import {
  downloadAndUnzipVSCode,
  resolveCliArgsFromVSCodeExecutablePath,
  runTests,
  SilentReporter,
} from "@vscode/test-electron";

process.env.DONT_PROMPT_WSL_INSTALL = "1";
delete process.env.VSCODE_IPC_HOOK_CLI;

const extensionPath = dirname(fileURLToPath(import.meta.url));
const workspacePath = requireEnvironment("CSLS_VSCODE_WORKSPACE_PATH");
const userDataPath = requireEnvironment("CSLS_VSCODE_USER_DATA_PATH");
const extensionsPath = requireEnvironment("CSLS_VSCODE_EXTENSIONS_PATH");
const cachePath = requireEnvironment("CSLS_VSCODE_CACHE_PATH");
const remoteServerRoot = process.env.CSLS_VSCODE_REMOTE_SERVER_ROOT;
const remoteDataPath = process.env.CSLS_VSCODE_REMOTE_DATA_PATH;
const resultsViewUi = process.env.CSLS_VSCODE_SUITE === "dist/results-view-suite.cjs";
const devToolsEndpointPath = resolve(userDataPath, "results-view-devtools-endpoint");
const executablePath = await downloadAndUnzipVSCode({
  cachePath,
  reporter: new SilentReporter(),
  timeout: 120_000,
  version: "stable",
});
const extensionPackages = resolveExtensionPackages();
if (remoteServerRoot === undefined) {
  for (const packagePath of extensionPackages) {
    installExtension(executablePath, packagePath, userDataPath, extensionsPath);
  }
} else {
  if (remoteDataPath === undefined || remoteDataPath.length === 0) {
    throw new Error("CSLS_VSCODE_REMOTE_DATA_PATH is required for remote testing.");
  }

  for (const packagePath of extensionPackages) {
    installRemoteExtension(remoteServerRoot, remoteDataPath, packagePath);
  }

  const testPackagePath = requireEnvironment("CSLS_VSCODE_REMOTE_TEST_EXTENSION_PATH");
  installRemoteExtension(remoteServerRoot, remoteDataPath, testPackagePath);
}

await runTests({
  cachePath,
  extensionDevelopmentPath: remoteServerRoot === undefined
    ? extensionPath
    : resolve(extensionPath, "remote-resolver"),
  extensionTestsEnv: {
    ...(resultsViewUi ? { CSLS_VSCODE_CDP_ENDPOINT_PATH: devToolsEndpointPath } : {}),
    ...copyEnvironment(
      "CSLS_VSCODE_EXPECTED_HOST",
      "CSLS_VSCODE_ORACLE_DIAGNOSTICS_TIMEOUT_MILLISECONDS",
      "CSLS_VSCODE_ORACLE_OUTPUT_PATH",
      "CSLS_VSCODE_ORACLE_PROFILE",
      "CSLS_VSCODE_REMOTE_DATA_PATH",
      "CSLS_VSCODE_REMOTE_RESULT_PATH",
      "CSLS_VSCODE_REMOTE_SERVER_ROOT",
      "CSLS_VSCODE_REMOTE_SUITE",
    ),
  },
  extensionTestsPath: resolve(
    extensionPath,
    process.env.CSLS_VSCODE_SUITE ??
      (remoteServerRoot === undefined ? "dist/suite.cjs" : "remote-controller.cjs"),
  ),
  launchArgs: [
    remoteServerRoot === undefined
      ? workspacePath
      : "--folder-uri=vscode-remote://test+csls" + pathToFileURL(workspacePath).pathname,
    "--disable-gpu",
    "--disable-telemetry",
    "--disable-updates",
    "--disable-workspace-trust",
    "--extensions-dir=" + extensionsPath,
    "--skip-release-notes",
    "--skip-welcome",
    "--user-data-dir=" + userDataPath,
    ...(resultsViewUi
      ? ["--remote-debugging-address=127.0.0.1", "--remote-debugging-port=0"]
      : []),
    ...(remoteServerRoot === undefined
      ? []
      : ["--enable-proposed-api=csls-tests.csls-test-resolver"]),
  ],
  ...(resultsViewUi ? { stderr: observeDevToolsEndpoint(devToolsEndpointPath) } : {}),
  vscodeExecutablePath: executablePath,
});

function observeDevToolsEndpoint(endpointPath) {
  let pendingText = "";
  let published = false;
  return new Writable({
    write(chunk, _encoding, callback) {
      process.stderr.write(chunk);
      pendingText = (pendingText + chunk.toString()).slice(-8_192);
      const endpoint = /DevTools listening on (ws:\/\/127\.0\.0\.1:\d+\/devtools\/browser\/[^\s]+)/.exec(pendingText)?.[1];
      if (published || endpoint === undefined) {
        callback();
        return;
      }

      published = true;
      const temporaryPath = endpointPath + ".tmp";
      writeFile(temporaryPath, endpoint, "utf8")
        .then(() => rename(temporaryPath, endpointPath))
        .then(() => callback(), callback);
    },
  });
}

function resolveExtensionPackages() {
  const serializedPackages = process.env.CSLS_VSCODE_EXTENSION_PATHS;
  if (serializedPackages !== undefined && serializedPackages.length > 0) {
    const packages = JSON.parse(serializedPackages);
    if (
      !Array.isArray(packages) ||
      packages.length === 0 ||
      packages.some((packagePath) => typeof packagePath !== "string" || packagePath.length === 0)
    ) {
      throw new Error("CSLS_VSCODE_EXTENSION_PATHS must contain a JSON array of paths.");
    }

    return packages;
  }

  return [
    requireEnvironment("CSLS_VSCODE_RUNTIME_EXTENSION_PATH"),
    requireEnvironment("CSLS_VSCODE_EXTENSION_PATH"),
  ];
}

function installRemoteExtension(serverRoot, serverDataPath, packagePath) {
  const extensionsPath = resolve(serverDataPath, "extensions");
  const result = spawnSync(
    resolve(serverRoot, "node"),
    [
      resolve(serverRoot, "out", "server-main.js"),
      "--accept-server-license-terms",
      "--extensions-dir",
      extensionsPath,
      "--force",
      "--install-extension",
      packagePath,
      "--server-data-dir",
      serverDataPath,
    ],
    { encoding: "utf8" },
  );
  if (result.status !== 0) {
    throw new Error(
      `VS Code Server could not install ${packagePath}: ${result.stderr || result.stdout}`,
    );
  }
}

function copyEnvironment(...names) {
  return Object.fromEntries(
    names
      .filter((name) => process.env[name] !== undefined)
      .map((name) => [name, process.env[name]]),
  );
}

function installExtension(executablePath, packagePath, userDataPath, extensionsPath) {
  const [command, ...prefixArguments] = resolveCliArgsFromVSCodeExecutablePath(executablePath);
  const result = spawnSync(
    command,
    [
      ...prefixArguments,
      "--extensions-dir",
      extensionsPath,
      "--force",
      "--install-extension",
      packagePath,
      "--user-data-dir",
      userDataPath,
    ],
    {
      encoding: "utf8",
      shell: process.platform === "win32",
    },
  );
  if (result.status !== 0) {
    throw new Error(
      `VS Code could not install ${packagePath}: ${result.stderr || result.stdout}`,
    );
  }
}

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
