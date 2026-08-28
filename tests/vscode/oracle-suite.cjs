const assert = require("node:assert/strict");
const { writeFile } = require("node:fs/promises");
const vscode = require("vscode");

const diagnosticsTimeoutMilliseconds = Number(
  process.env.CSLS_VSCODE_ORACLE_DIAGNOSTICS_TIMEOUT_MILLISECONDS ?? 120_000,
);
const requestTimeoutMilliseconds = 120_000;

exports.run = async function run() {
  const profile = requireEnvironment("CSLS_VSCODE_ORACLE_PROFILE");
  const outputPath = requireEnvironment("CSLS_VSCODE_ORACLE_OUTPUT_PATH");
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert.ok(workspaceFolder, "The VS Code oracle workspace must be open.");
  const documentUri = vscode.Uri.joinPath(workspaceFolder.uri, "Program.cs");
  const csharpApi = await activateProfile(profile);
  const document = await vscode.workspace.openTextDocument(documentUri);
  await vscode.window.showTextDocument(document);
  assert.equal(document.languageId, "csharp");

  const text = document.getText();
  const consoleOffset = requireOffset(text, "System.Console") + "System.".length;
  const completionOffset = requireOffset(text, "System.Console.") + "System.Console.".length;
  const simplifyStart = requireOffset(text, "System.Console");
  const simplifyEnd = simplifyStart + "System.Console".length;
  const diagnosticsPromise = csharpApi === undefined
    ? waitForCompilerDiagnostics(documentUri)
    : requestMicrosoftDiagnostics(csharpApi, documentUri);

  const hovers = await executeWithTimeout(
    "vscode.executeHoverProvider",
    documentUri,
    document.positionAt(consoleOffset),
  );
  const completions = await executeWithTimeout(
    "vscode.executeCompletionItemProvider",
    documentUri,
    document.positionAt(completionOffset),
  );
  const codeActions = await executeWithTimeout(
    "vscode.executeCodeActionProvider",
    documentUri,
    new vscode.Range(
      document.positionAt(simplifyStart),
      document.positionAt(simplifyEnd),
    ),
  );
  const inlayHints = await executeWithTimeout(
    "vscode.executeInlayHintProvider",
    documentUri,
    new vscode.Range(document.positionAt(0), document.positionAt(text.length)),
  );
  const diagnostics = await diagnosticsPromise;
  const observation = {
    profile,
    diagnostics: normalizeDiagnostics(diagnostics),
    hoverText: normalizeHoverText(hovers),
    completionLabels: normalizeCompletionLabels(completions),
    codeActionTitles: normalizeCodeActionTitles(codeActions),
    inlayHintLabels: normalizeInlayHintLabels(inlayHints),
  };
  await writeFile(outputPath, JSON.stringify(observation, null, 2) + "\n", "utf8");
};

async function activateProfile(profile) {
  const runtimeExtension = vscode.extensions.getExtension(
    "ms-dotnettools.vscode-dotnet-runtime",
  );
  assert.ok(runtimeExtension, "The .NET Install Tool must be installed in every profile.");
  if (profile === "csls") {
    const extension = vscode.extensions.getExtension("willibrandon.csls");
    assert.ok(extension, "The packaged csls extension must be installed.");
    await withTimeout(extension.activate(), "csls extension activation");
    return undefined;
  }

  const csharpExtension = vscode.extensions.getExtension("ms-dotnettools.csharp");
  assert.ok(csharpExtension, "The Microsoft C# extension must be installed.");
  if (profile === "csdevkit") {
    const devKitExtension = vscode.extensions.getExtension("ms-dotnettools.csdevkit");
    assert.ok(devKitExtension, "C# Dev Kit must be installed in its oracle profile.");
    await withTimeout(devKitExtension.activate(), "C# Dev Kit activation");
  } else {
    assert.equal(profile, "csharp", `Unknown VS Code oracle profile: ${profile}`);
  }

  const csharpApi = await withTimeout(
    csharpExtension.activate(),
    "Microsoft C# extension activation",
  );
  assert.notEqual(
    csharpApi?.isLimitedActivation,
    true,
    "The Microsoft C# extension must have workspace trust.",
  );
  assert.equal(
    typeof csharpApi?.initializationFinished,
    "function",
    "The Microsoft C# extension must expose its initialization gate.",
  );
  await withTimeout(
    csharpApi.initializationFinished(),
    "Microsoft C# project initialization",
  );
  return csharpApi;
}

async function requestMicrosoftDiagnostics(csharpApi, documentUri) {
  const cancellation = new vscode.CancellationTokenSource();
  try {
    const deadline = Date.now() + diagnosticsTimeoutMilliseconds;
    let lastDiagnosticCodes = [];
    let lastProjectContexts = [];
    while (Date.now() < deadline) {
      const contextList = await withTimeout(
        csharpApi.experimental.sendServerRequest(
          {
            method: "textDocument/_vs_getProjectContexts",
            parameterStructures: "byName",
          },
          { _vs_textDocument: { uri: documentUri.toString() } },
          cancellation.token,
        ),
        "Microsoft C# project context",
      );
      const contexts = contextList?._vs_projectContexts ?? [];
      lastProjectContexts = contexts;
      const defaultContext = contexts[contextList?._vs_defaultIndex];
      const projectContext = defaultContext?._vs_is_miscellaneous !== true
        ? defaultContext
        : contexts.find((context) => context._vs_is_miscellaneous !== true);
      if (projectContext !== undefined) {
        const report = await withTimeout(
          csharpApi.experimental.sendServerRequest(
            {
              method: "textDocument/diagnostic",
            parameterStructures: "byName",
          },
          {
              identifier: "DocumentCompilerSemantic",
              textDocument: {
                uri: documentUri.toString(),
                _vs_projectContext: projectContext,
              },
            },
            cancellation.token,
          ),
          "Microsoft C# diagnostics",
        );
        assert.equal(report.kind, "full");
        lastDiagnosticCodes = report.items.map((diagnostic) =>
          normalizeDiagnosticCode(diagnostic.code),
        );
        if (lastDiagnosticCodes.includes("CS0103")) {
          return report.items.map((diagnostic) => ({
            ...diagnostic,
            severity: diagnostic.severity === undefined
              ? undefined
              : diagnostic.severity - 1,
          }));
        }
      }

      await delay(100);
    }

    throw new Error(
      `The Microsoft C# project never reported CS0103. Last contexts: ${JSON.stringify(lastProjectContexts)}. Last diagnostics: ${lastDiagnosticCodes.join(", ")}`,
    );
  } finally {
    cancellation.dispose();
  }
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function waitForCompilerDiagnostics(documentUri) {
  return new Promise((resolve, reject) => {
    let settled = false;
    const timeout = setTimeout(() => {
      if (!settled) {
        settled = true;
        subscription.dispose();
        reject(new Error("VS Code did not publish compiler diagnostics."));
      }
    }, diagnosticsTimeoutMilliseconds);
    const inspect = () => {
      const diagnostics = vscode.languages.getDiagnostics(documentUri);
      if (diagnostics.some((diagnostic) => normalizeDiagnosticCode(diagnostic.code) === "CS0103")) {
        settled = true;
        clearTimeout(timeout);
        subscription.dispose();
        resolve(diagnostics);
      }
    };
    const subscription = vscode.languages.onDidChangeDiagnostics((event) => {
      if (event.uris.some((uri) => uri.toString() === documentUri.toString())) {
        inspect();
      }
    });
    inspect();
  });
}

async function executeWithTimeout(command, ...commandArguments) {
  return withTimeout(vscode.commands.executeCommand(command, ...commandArguments), command);
}

async function withTimeout(operation, description) {
  let timeout;
  try {
    return await Promise.race([
      operation,
      new Promise((_, reject) => {
        timeout = setTimeout(
          () => reject(new Error(`${description} timed out.`)),
          requestTimeoutMilliseconds,
        );
      }),
    ]);
  } finally {
    clearTimeout(timeout);
  }
}

function normalizeDiagnostics(diagnostics) {
  return diagnostics
    .map((diagnostic) => ({
      code: normalizeDiagnosticCode(diagnostic.code),
      message: diagnostic.message,
      severity: diagnostic.severity,
      tags: [...(diagnostic.tags ?? [])].sort((left, right) => left - right),
    }))
    .sort((left, right) =>
      left.code.localeCompare(right.code) || left.message.localeCompare(right.message),
    );
}

function normalizeDiagnosticCode(code) {
  if (code === undefined) {
    return "";
  }

  if (typeof code === "object" && code !== null && "value" in code) {
    return String(code.value);
  }

  return String(code);
}

function normalizeHoverText(hovers) {
  return (hovers ?? [])
    .flatMap((hover) => hover.contents)
    .map((content) => {
      if (typeof content === "string") {
        return content;
      }

      return content.value;
    })
    .join("\n");
}

function normalizeCompletionLabels(completions) {
  const items = Array.isArray(completions) ? completions : completions?.items ?? [];
  return items
    .map((item) => (typeof item.label === "string" ? item.label : item.label.label))
    .filter((label) => label === "WriteLine")
    .sort();
}

function normalizeCodeActionTitles(codeActions) {
  return (codeActions ?? [])
    .map((action) => action.title)
    .filter((title) => typeof title === "string")
    .sort();
}

function normalizeInlayHintLabels(inlayHints) {
  return (inlayHints ?? [])
    .map((hint) =>
      typeof hint.label === "string"
        ? hint.label
        : hint.label.map((part) => part.value).join(""),
    )
    .sort();
}

function requireOffset(text, value) {
  const offset = text.indexOf(value);
  assert.notEqual(offset, -1, `The fixture must contain ${value}.`);
  return offset;
}

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
