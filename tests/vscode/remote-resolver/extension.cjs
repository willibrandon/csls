const { randomBytes } = require("node:crypto");
const { spawn } = require("node:child_process");
const { join } = require("node:path");
const vscode = require("vscode");

let serverProcess;

exports.activate = function activate(context) {
  const output = vscode.window.createOutputChannel("csls remote test resolver");
  context.subscriptions.push(output);
  context.subscriptions.push({ dispose: stopServer });
  context.subscriptions.push(
    vscode.workspace.registerRemoteAuthorityResolver("test", {
      resolve: () => resolveAuthority(output),
    }),
  );
};

async function resolveAuthority(output) {
  if (serverProcess !== undefined) {
    throw vscode.RemoteAuthorityResolverError.TemporarilyNotAvailable(
      "The csls test resolver already has a VS Code server process.",
    );
  }

  const serverRoot = requireEnvironment("CSLS_VSCODE_REMOTE_SERVER_ROOT");
  const serverDataPath = requireEnvironment("CSLS_VSCODE_REMOTE_DATA_PATH");
  const connectionToken = randomBytes(32).toString("hex");
  const nodePath = join(serverRoot, "node");
  const serverMainPath = join(serverRoot, "out", "server-main.js");
  const serverArguments = [
    serverMainPath,
    "--host=127.0.0.1",
    "--port=0",
    "--disable-telemetry",
    "--disable-experiments",
    "--use-host-proxy",
    "--accept-server-license-terms",
    "--server-data-dir",
    serverDataPath,
    "--connection-token",
    connectionToken,
  ];
  output.appendLine(`Starting ${nodePath} ${serverArguments.join(" ")}`);
  serverProcess = spawn(nodePath, serverArguments, {
    cwd: serverRoot,
    env: process.env,
    stdio: ["ignore", "pipe", "pipe"],
  });

  return await new Promise((resolve, reject) => {
    let settled = false;
    let currentLine = "";
    const timeout = setTimeout(() => {
      fail("The VS Code remote server did not start within 120 seconds.");
    }, 120_000);

    function processOutput(chunk) {
      const text = chunk.toString();
      output.append(text);
      currentLine += text;
      const lines = currentLine.split(/\r?\n/u);
      currentLine = lines.pop() ?? "";
      for (const line of lines) {
        const match = /Extension host agent listening on (\d+)/u.exec(line);
        if (match !== null && !settled) {
          settled = true;
          clearTimeout(timeout);
          resolve(new vscode.ResolvedAuthority(
            "127.0.0.1",
            Number.parseInt(match[1], 10),
            connectionToken,
          ));
        }
      }
    }

    function fail(message) {
      output.appendLine(message);
      if (!settled) {
        settled = true;
        clearTimeout(timeout);
        stopServer();
        reject(vscode.RemoteAuthorityResolverError.NotAvailable(message, true));
      }
    }

    serverProcess.stdout.on("data", processOutput);
    serverProcess.stderr.on("data", processOutput);
    serverProcess.once("error", (error) => fail(error.message));
    serverProcess.once("exit", (code, signal) => {
      serverProcess = undefined;
      fail(`The VS Code remote server exited with code ${code} and signal ${signal}.`);
    });
  });
}

function stopServer() {
  if (serverProcess !== undefined) {
    serverProcess.kill();
    serverProcess = undefined;
  }
}

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
