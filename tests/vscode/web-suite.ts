import * as vscode from "vscode";

const languageFeatureTimeoutMilliseconds = 30_000;
const frameworkTypeDocumentText = `using System;
using System.Reflection;

class BrowserCreated
{
    private static readonly Lazy<(
        ConstructorInfo MemberAnalysisConstructor,
        ConstructorInfo OptionsConstructor,
        ConstructorInfo ActionConstructor,
        MethodInfo FormattingOptionsMethod,
        MethodInfo ImmutableArrayCreateMethod)> s_contract = new();

    static void Apply(Func<string, string> transform)
    {
        (
            ConstructorInfo memberAnalysisConstructor,
            ConstructorInfo optionsConstructor,
            ConstructorInfo actionConstructor,
            MethodInfo formattingOptionsMethod,
            MethodInfo immutableArrayCreateMethod) = s_contract.Value;
    }
}
`;

export async function run(): Promise<void> {
  await runFeatureContract({
    expectedHost: "browser",
    requireRuntimeExtension: false,
  });
}

export async function runSemanticHighlightingContract(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The VS Code integration workspace must be open.");
  assert(
    vscode.workspace
      .getConfiguration("workbench")
      .get<string>("colorTheme") === "csls Theme Without Semantic Highlighting",
    "The semantic-highlighting regression must run with a theme that does not opt in.",
  );
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The csls extension must be installed.");
  const documentUri = vscode.Uri.joinPath(workspaceFolder.uri, "Program.cs");
  const document = await vscode.workspace.openTextDocument(documentUri);
  await vscode.window.showTextDocument(document);
  assert(document.languageId === "csharp", "The document must use the C# language mode.");
  assert(
    vscode.workspace
      .getConfiguration("editor", document)
      .get<boolean | string>("semanticHighlighting.enabled") === true,
    "The csls extension must enable semantic highlighting for C# without user configuration.",
  );

  await extension.activate();
  await replaceDocumentText(document, frameworkTypeDocumentText);
  const legend = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokensLegend | undefined>(
      "vscode.provideDocumentSemanticTokensLegend",
      document.uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token legend provider did not complete.",
  );
  const tokens = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokens | undefined>(
      "vscode.provideDocumentSemanticTokens",
      document.uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token provider did not complete.",
  );
  assert(
    legend !== undefined && tokens !== undefined,
    "csls must return semantic tokens for the visible C# document.",
  );
  assertAllSemanticTokenTypes(document, frameworkTypeDocumentText, tokens, legend, "Lazy", "class");
  assertAllSemanticTokenTypes(document, frameworkTypeDocumentText, tokens, legend, "Func", "type");
  assertAllSemanticTokenTypes(
    document,
    frameworkTypeDocumentText,
    tokens,
    legend,
    "ConstructorInfo",
    "class",
  );
  assertAllSemanticTokenTypes(
    document,
    frameworkTypeDocumentText,
    tokens,
    legend,
    "MethodInfo",
    "class",
  );
}

export interface FeatureContractOptions {
  readonly expectedHost: "browser" | "desktop" | "remote";
  readonly requireRuntimeExtension: boolean;
}

export async function runFeatureContract(options: FeatureContractOptions): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The VS Code integration workspace must be open.");
  if (options.requireRuntimeExtension) {
    assert(
      vscode.extensions.getExtension("ms-dotnettools.vscode-dotnet-runtime") !== undefined,
      "The .NET Install Tool must be installed.",
    );
  }

  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The csls extension must be installed.");
  const documentUri = vscode.Uri.joinPath(workspaceFolder.uri, "Program.cs");
  const document = await vscode.workspace.openTextDocument(documentUri);
  await vscode.window.showTextDocument(document);
  assert(document.languageId === "csharp", "The document must use the C# language mode.");
  assert(
    vscode.workspace
      .getConfiguration("editor", document)
      .get<boolean | string>("semanticHighlighting.enabled") === true,
    "The csls extension must enable semantic highlighting for C# without user configuration.",
  );

  const api: unknown = await extension.activate();
  assert(extension.isActive, "The csls extension must be active.");
  assert(isExtensionApi(api), "The csls extension must return its host API.");
  assert(api.host === options.expectedHost, `Expected the ${options.expectedHost} host.`);
  assert(api.state === 2, "The csls language client must be running.");
  await assertProjectDiscovery(api, workspaceFolder);
  if (options.expectedHost !== "browser") {
    const expectedServerPath = vscode.Uri.joinPath(
      extension.extensionUri,
      "server",
      process.platform === "win32" ? "csls.exe" : "csls",
    ).fsPath;
    assert(
      api.serverPath === expectedServerPath,
      `Expected csls to start its bundled server at ${expectedServerPath}, received ${api.serverPath}.`,
    );
    assert(
      typeof api.runtimePath === "string" && api.runtimePath.length > 0,
      "The .NET Install Tool must resolve a runtime.",
    );
    assert(
      typeof api.sdkPath === "string" && api.sdkPath.length > 0,
      "The .NET Install Tool must resolve an SDK.",
    );
    await assertSolutionExperience(api, workspaceFolder, documentUri);
  }
  await assertConsoleHover(documentUri);
  await assertConsoleCompletion(documentUri);
  await assertDefinition(document);
  await assertFrameworkDefinitionOpens(document);
  if (options.expectedHost !== "browser") {
    await assertLazyFrameworkDefinitionOpens(document);
  }
  await assertExtensionMethodDefinitionOpens(document);
  await assertSemanticTokens(document);
  await assertConfigurableInlayHints(document);
  await assertDiagnosticsTrackEdits(document);
  await assertFormatting(document);
  await assertRename(document);
  await assertCodeAction(document);
  await assertCreatedFileIsLoaded(workspaceFolder);

  await replaceDocumentText(document, 'Console.WriteLine("hello");\n');
  await vscode.commands.executeCommand("csls.restartServer");
  await assertConsoleHover(documentUri);
}

async function assertProjectDiscovery(api: {
  readonly projects?: () => readonly {
    readonly name: string;
    readonly path: string;
  }[];
}, workspaceFolder: vscode.WorkspaceFolder): Promise<void> {
  assert(typeof api.projects === "function", "The Solution view must expose loaded projects.");
  await vscode.commands.executeCommand("csls.refreshSolution");
  const projects = api.projects();
  assert(
    projects.some((project) => project.name === "Fixture"),
    `The Solution view must contain the Roslyn-loaded Fixture project. Received ${JSON.stringify(projects)}.`,
  );
  const toolPath = vscode.Uri.joinPath(workspaceFolder.uri, "Tools", "Tool.cs").fsPath;
  assert(
    projects.some((project) => project.name === "Tool.cs" && project.path === toolPath),
    `The Solution view must expose every discovered file-based app by its source path. Received ${JSON.stringify(projects)}.`,
  );
}

async function assertSolutionExperience(
  api: {
    readonly projects?: () => readonly {
      readonly name: string;
      readonly path: string;
    }[];
    readonly tests?: () => readonly string[];
    readonly testErrors?: () => readonly string[];
  },
  workspaceFolder: vscode.WorkspaceFolder,
  documentUri: vscode.Uri,
): Promise<void> {
  assert(typeof api.projects === "function", "The desktop extension must expose loaded projects.");
  const project = api.projects().find((candidate) => candidate.name === "Fixture");
  assert(project !== undefined, "The Solution view must contain the Roslyn-loaded Fixture project.");
  assert(typeof api.tests === "function", "The desktop extension must expose discovered tests.");
  await waitUntil(
    () => api.tests?.().includes("RunsFromVsCode") === true,
    "The Testing view did not discover the real Microsoft Testing Platform test.",
  );
  const normalDiscoveryTarget = vscode.Uri.joinPath(
    workspaceFolder.uri,
    "Tests",
    "bin",
    "Debug",
    "net10.0",
    "Fixture.Tests.dll",
  );
  assert(
    !(await exists(normalDiscoveryTarget)),
    `Automatic test discovery must not build into the workspace target path ${normalDiscoveryTarget.fsPath}.`,
  );
  const buildTarget = {
    projectPath: project.path,
    workspaceRoot: workspaceFolder.uri.fsPath,
  };
  await Promise.all([
    vscode.commands.executeCommand("csls.build", buildTarget),
    vscode.commands.executeCommand("csls.build", buildTarget),
  ]);
  await Promise.all([
    vscode.commands.executeCommand("csls.test"),
    vscode.commands.executeCommand("csls.test"),
  ]);
  assert(
    api.tests().includes("RunsFromVsCode"),
    "The Testing view must discover the real Microsoft Testing Platform test.",
  );
  await assertDebugging(project.path, workspaceFolder, documentUri);
}

async function exists(uri: vscode.Uri): Promise<boolean> {
  try {
    await vscode.workspace.fs.stat(uri);
    return true;
  } catch {
    return false;
  }
}

async function assertDebugging(
  projectPath: string,
  workspaceFolder: vscode.WorkspaceFolder,
  documentUri: vscode.Uri,
): Promise<void> {
  const breakpoint = new vscode.SourceBreakpoint(
    new vscode.Location(documentUri, new vscode.Position(0, 0)),
  );
  vscode.debug.addBreakpoints([breakpoint]);
  let session: vscode.DebugSession | undefined;
  let startListener: vscode.Disposable | undefined;
  const started = new Promise<vscode.DebugSession>((resolve) => {
    startListener = vscode.debug.onDidStartDebugSession((candidate) => {
      session = candidate;
      startListener?.dispose();
      startListener = undefined;
      resolve(candidate);
    });
  });
  let terminationListener: vscode.Disposable | undefined;
  const terminated = new Promise<void>((resolve) => {
    terminationListener = vscode.debug.onDidTerminateDebugSession((candidate) => {
      if (candidate === session) {
        terminationListener?.dispose();
        terminationListener = undefined;
        resolve();
      }
    });
  });
  try {
    await vscode.commands.executeCommand("csls.debug", {
      projectPath,
      workspaceRoot: workspaceFolder.uri.fsPath,
    });
    session = await withTimeout(
      started,
      languageFeatureTimeoutMilliseconds,
      "The .NET debug session did not start.",
    );
    await waitUntil(() => {
      return vscode.debug.activeStackItem instanceof vscode.DebugStackFrame;
    }, "The .NET debugger did not stop at the C# source breakpoint.");
    await vscode.commands.executeCommand("workbench.action.debug.continue");
    await withTimeout(
      terminated,
      languageFeatureTimeoutMilliseconds,
      "The .NET debug session did not terminate.",
    );
  } finally {
    startListener?.dispose();
    terminationListener?.dispose();
    vscode.debug.removeBreakpoints([breakpoint]);
    if (vscode.debug.activeDebugSession !== undefined) {
      await vscode.debug.stopDebugging(vscode.debug.activeDebugSession);
    }
  }
}

async function assertDefinition(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(
    document,
    "class Widget {}\nclass Consumer { Widget? Value { get; } }\n",
  );
  const definitions = await withTimeout(
    vscode.commands.executeCommand<readonly (vscode.Location | vscode.LocationLink)[]>(
      "vscode.executeDefinitionProvider",
      document.uri,
      new vscode.Position(1, 17),
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code definition provider did not complete.",
  );
  const definitionUris = definitions.map((definition) =>
    ("uri" in definition ? definition.uri : definition.targetUri).toString());
  assert(
    definitionUris.includes(document.uri.toString()),
    `csls must navigate from a type reference to its source definition in VS Code. Received definitions: ${JSON.stringify(definitionUris)}. Diagnostics: ${JSON.stringify(vscode.languages.getDiagnostics(document.uri).map((diagnostic) => diagnostic.message))}.`,
  );
}

async function assertFrameworkDefinitionOpens(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(document, 'Console.WriteLine("hello");\n');
  const definitions = await withTimeout(
    vscode.commands.executeCommand<readonly (vscode.Location | vscode.LocationLink)[]>(
      "vscode.executeDefinitionProvider",
      document.uri,
      new vscode.Position(0, 2),
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code framework definition provider did not complete.",
  );
  const definition = definitions.find((candidate) =>
    ("uri" in candidate ? candidate.uri : candidate.targetUri).scheme === "csharp");
  assert(
    definition !== undefined,
    `csls must return a virtual C# document for framework definitions. Received ${JSON.stringify(
      definitions.map((candidate) =>
        ("uri" in candidate ? candidate.uri : candidate.targetUri).toString()),
    )}.`,
  );
  const definitionUri = "uri" in definition ? definition.uri : definition.targetUri;
  const metadataDocument = await withTimeout(
    vscode.workspace.openTextDocument(definitionUri),
    languageFeatureTimeoutMilliseconds,
    "VS Code could not open the csls framework definition.",
  );
  assert(
    metadataDocument.languageId === "csharp",
    `The framework definition must use the C# language mode, not ${metadataDocument.languageId}.`,
  );
  assert(
    metadataDocument.getText().includes("class Console"),
    "The framework definition must contain the System.Console declaration.",
  );
  await vscode.window.showTextDocument(metadataDocument);

  const providerRequests = [
    {
      method: "textDocument/documentLink",
      request: vscode.commands.executeCommand<readonly vscode.DocumentLink[]>(
        "vscode.executeLinkProvider",
        metadataDocument.uri,
      ),
    },
    {
      method: "textDocument/foldingRange",
      request: vscode.commands.executeCommand<readonly vscode.FoldingRange[]>(
        "vscode.executeFoldingRangeProvider",
        metadataDocument.uri,
      ),
    },
  ] as const;
  const providerFailures = await withTimeout(
    Promise.all(providerRequests.map(async (provider) => {
      try {
        await provider.request;
        return undefined;
      } catch (error) {
        return `${provider.method}: ${String(error)}`;
      }
    })),
    languageFeatureTimeoutMilliseconds,
    "VS Code did not complete the virtual C# document providers.",
  );
  const failures = providerFailures.filter((failure) => failure !== undefined);
  assert(
    failures.length === 0,
    `VS Code providers failed for the open csharp: framework document: ${failures.join(" | ")}`,
  );
}

async function assertExtensionMethodDefinitionOpens(
  document: vscode.TextDocument,
): Promise<void> {
  const source = "var first = new[] { 1 }.FirstOrDefault();\n";
  await replaceDocumentText(document, source);
  const definitions = await withTimeout(
    vscode.commands.executeCommand<readonly (vscode.Location | vscode.LocationLink)[]>(
      "vscode.executeDefinitionProvider",
      document.uri,
      document.positionAt(source.indexOf("FirstOrDefault")),
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code extension-method definition provider did not complete.",
  );
  const definition = definitions.find((candidate) =>
    ("uri" in candidate ? candidate.uri : candidate.targetUri).scheme === "csharp");
  assert(
    definition !== undefined,
    `csls must return Enumerable.FirstOrDefault metadata in VS Code. Received ${JSON.stringify(
      definitions.map((candidate) =>
        ("uri" in candidate ? candidate.uri : candidate.targetUri).toString()),
    )}.`,
  );
  const definitionUri = "uri" in definition ? definition.uri : definition.targetUri;
  const metadataDocument = await withTimeout(
    vscode.workspace.openTextDocument(definitionUri),
    languageFeatureTimeoutMilliseconds,
    "VS Code could not open the FirstOrDefault metadata definition.",
  );
  assert(
    metadataDocument.getText().includes("FirstOrDefault"),
    "The extension-method definition must contain Enumerable.FirstOrDefault.",
  );
}

async function assertLazyFrameworkDefinitionOpens(
  document: vscode.TextDocument,
): Promise<void> {
  const source = "Lazy<int> value = new();\n";
  await replaceDocumentText(document, source);
  const definitions = await withTimeout(
    vscode.commands.executeCommand<readonly (vscode.Location | vscode.LocationLink)[]>(
      "vscode.executeDefinitionProvider",
      document.uri,
      document.positionAt(source.indexOf("Lazy")),
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code Lazy definition provider did not complete.",
  );
  const definition = definitions.find((candidate) =>
    ("uri" in candidate ? candidate.uri : candidate.targetUri).scheme === "csharp");
  assert(
    definition !== undefined,
    `csls must return Lazy.cs in VS Code. Received ${JSON.stringify(
      definitions.map((candidate) =>
        ("uri" in candidate ? candidate.uri : candidate.targetUri).toString()),
    )}.`,
  );
  const definitionUri = "uri" in definition ? definition.uri : definition.targetUri;
  assert(
    definitionUri.path.endsWith("/Lazy.cs"),
    `The Lazy definition URI must end in /Lazy.cs, received ${definitionUri.toString()}.`,
  );
  const metadataDocument = await withTimeout(
    vscode.workspace.openTextDocument(definitionUri),
    languageFeatureTimeoutMilliseconds,
    "VS Code could not open Lazy.cs.",
  );
  assert(
    metadataDocument.getText().includes("private T CreateValue()"),
    "Lazy.cs must contain the runtime implementation rather than a metadata signature shell.",
  );
}

async function assertSemanticTokens(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(document, "class Widget { string Name { get; } = \"hello\"; }\n");
  const legend = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokensLegend | undefined>(
      "vscode.provideDocumentSemanticTokensLegend",
      document.uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token legend provider did not complete.",
  );
  const tokens = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokens | undefined>(
      "vscode.provideDocumentSemanticTokens",
      document.uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token provider did not complete.",
  );
  assert(
    legend?.tokenTypes.includes("class") === true &&
      tokens !== undefined &&
      tokens.data.length >= 5,
    "csls must return semantic C# tokens in VS Code.",
  );
}

async function assertConfigurableInlayHints(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(
    document,
    "class Example { static void Print(string message) {} void Run() { Print(\"hello\"); var count = 1; } }\n",
  );
  const configuration = vscode.workspace.getConfiguration("csls", document.uri);
  await configuration.update(
    "inlayHints.enableInlayHintsForTypes",
    true,
    vscode.ConfigurationTarget.Global,
  );
  await waitUntil(
    () => vscode.workspace
      .getConfiguration("csls", document.uri)
      .get<boolean>("inlayHints.enableInlayHintsForTypes") === true,
    "VS Code did not retain the updated type-hint configuration.",
  );
  await waitUntil(async () =>
    (await getInlayHintLabels(document)).includes("int"),
  "csls did not apply the updated type-hint configuration in VS Code.");

  await vscode.workspace.getConfiguration("csls", document.uri).update(
    "inlayHints.enableInlayHintsForParameters",
    true,
    vscode.ConfigurationTarget.Global,
  );
  await waitUntil(
    () => vscode.workspace
      .getConfiguration("csls", document.uri)
      .get<boolean>("inlayHints.enableInlayHintsForParameters") === true,
    "VS Code did not retain the updated parameter-hint configuration.",
  );
  let observedLabels: string[] = [];
  try {
    try {
      await waitUntil(async () => {
        observedLabels = await getInlayHintLabels(document);
        return observedLabels.includes("message:") && observedLabels.includes("int");
      }, "csls did not apply the updated inlay-hint configuration in VS Code.");
    } catch {
      throw new Error(
        `csls did not return the configured inlay hints in VS Code. Received: ${JSON.stringify(observedLabels)}.`,
      );
    }
  } finally {
    await configuration.update(
      "inlayHints.enableInlayHintsForParameters",
      false,
      vscode.ConfigurationTarget.Global,
    );
    await configuration.update(
      "inlayHints.enableInlayHintsForTypes",
      false,
      vscode.ConfigurationTarget.Global,
    );
  }
}

async function getInlayHintLabels(document: vscode.TextDocument): Promise<string[]> {
  const hints = await vscode.commands.executeCommand<readonly vscode.InlayHint[]>(
    "vscode.executeInlayHintProvider",
    document.uri,
    new vscode.Range(
      document.positionAt(0),
      document.positionAt(document.getText().length),
    ),
  );
  return hints.map(getInlayHintLabel);
}

async function assertConsoleCompletion(documentUri: vscode.Uri): Promise<void> {
  const completion = await withTimeout(
    vscode.commands.executeCommand<vscode.CompletionList>(
      "vscode.executeCompletionItemProvider",
      documentUri,
      new vscode.Position(0, 8),
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code completion provider did not complete.",
  );
  assert(
    completion.items.some((item) => getCompletionLabel(item) === "WriteLine"),
    "csls must complete System.Console members in VS Code.",
  );
}

async function assertDiagnosticsTrackEdits(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(document, "Console.WriteLine(missing);\n");
  await waitUntil(
    () => vscode.languages
      .getDiagnostics(document.uri)
      .some((diagnostic) => diagnostic.message.includes("missing")),
    "csls did not publish the edited document diagnostic in VS Code.",
  );

  await replaceDocumentText(document, 'Console.WriteLine("fixed");\n');
  await waitUntil(
    () => !vscode.languages
      .getDiagnostics(document.uri)
      .some((diagnostic) => diagnostic.message.includes("missing")),
    "csls did not clear the repaired document diagnostic in VS Code.",
  );
}

async function assertFormatting(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(
    document,
    'class Example{void Method(){Console.WriteLine("hello");}}\n',
  );
  const edits = await withTimeout(
    vscode.commands.executeCommand<readonly vscode.TextEdit[]>(
      "vscode.executeFormatDocumentProvider",
      document.uri,
      { insertSpaces: true, tabSize: 4 },
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code formatting provider did not complete.",
  );
  assert(edits.length > 0, "csls must return C# formatting edits in VS Code.");
  const workspaceEdit = new vscode.WorkspaceEdit();
  workspaceEdit.set(document.uri, [...edits]);
  assert(
    await vscode.workspace.applyEdit(workspaceEdit),
    "VS Code could not apply the C# formatting edits.",
  );
  assert(
    /class Example\s*\{\s+void Method\(\)\s*\{/u.test(document.getText()),
    "csls must format the class and method bodies in VS Code.",
  );
}

async function assertRename(document: vscode.TextDocument): Promise<void> {
  await replaceDocumentText(
    document,
    "class Widget { Widget? Value { get; set; } }\n",
  );
  const edit = await withTimeout(
    vscode.commands.executeCommand<vscode.WorkspaceEdit>(
      "vscode.executeDocumentRenameProvider",
      document.uri,
      new vscode.Position(0, 7),
      "Gadget",
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code rename provider did not complete.",
  );
  assert(
    await vscode.workspace.applyEdit(edit),
    "VS Code could not apply the C# rename edits.",
  );
  assert(
    document.getText().replaceAll("\r\n", "\n") ===
      "class Gadget { Gadget? Value { get; set; } }\n",
    "csls must rename the declaration and reference in VS Code.",
  );
}

async function assertCodeAction(document: vscode.TextDocument): Promise<void> {
  const text = 'class Example { void Method() { System.Console.WriteLine("hello"); } }\n';
  await replaceDocumentText(document, text);
  const start = text.indexOf("System.Console");
  await waitUntil(async () => {
    const hovers = await vscode.commands.executeCommand<readonly vscode.Hover[]>(
      "vscode.executeHoverProvider",
      document.uri,
      document.positionAt(start + "System.".length),
    );
    return getHoverText(hovers).includes("System.Console");
  }, "csls did not synchronize the code-action document in VS Code.");
  const actions = await withTimeout(
    vscode.commands.executeCommand<readonly vscode.CodeAction[]>(
      "vscode.executeCodeActionProvider",
      document.uri,
      new vscode.Range(0, start, 0, start + "System.Console".length),
      "quickfix",
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code code-action provider did not complete.",
  );
  assert(
    actions.some(
      (action) => action.title === "Simplify member access 'System.Console'",
    ),
    "csls must return the semantic simplify-name action in VS Code. " +
      `Received ${JSON.stringify(actions.map((action) => action.title))}.`,
  );

  const refactoringText = `internal sealed class DebuggerPackage
{
    internal DebuggerPackage(string identifier, Uri source)
    {
        Identifier = identifier;
        Source = source;
    }

    internal string Identifier { get; }

    internal Uri Source { get; }
}
`;
  await replaceDocumentText(document, refactoringText);
  const refactorings = await withTimeout(
    vscode.commands.executeCommand<readonly vscode.CodeAction[]>(
      "vscode.executeCodeActionProvider",
      document.uri,
      new vscode.Range(1, 0, 1, 0),
      "refactor",
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code refactoring provider did not complete.",
  );
  const refactoringTitles = refactorings.map((action) => action.title);
  assert(
    refactoringTitles.includes("Extract base class..."),
    "csls must return Roslyn's extract-base-class refactoring in VS Code. " +
      `Received ${JSON.stringify(refactoringTitles)}.`,
  );
  assert(
    refactoringTitles.includes("Convert to positional record"),
    "csls must return Roslyn's convert-to-record refactoring in VS Code. " +
      `Received ${JSON.stringify(refactoringTitles)}.`,
  );
  assert(
    refactoringTitles.includes("Add 'DebuggerDisplay' attribute"),
    "csls must return Roslyn's DebuggerDisplay refactoring in VS Code. " +
      `Received ${JSON.stringify(refactoringTitles)}.`,
  );
}

async function assertCreatedFileIsLoaded(
  workspaceFolder: vscode.WorkspaceFolder,
): Promise<void> {
  const uri = vscode.Uri.joinPath(workspaceFolder.uri, "BrowserCreated.cs");
  const source = frameworkTypeDocumentText;
  await vscode.workspace.fs.writeFile(
    uri,
    new TextEncoder().encode(source),
  );
  const document = await vscode.workspace.openTextDocument(uri);
  await vscode.window.showTextDocument(document);
  await waitUntil(async () => {
    const hovers = await vscode.commands.executeCommand<readonly vscode.Hover[]>(
      "vscode.executeHoverProvider",
      uri,
      document.positionAt(source.indexOf("BrowserCreated")),
    );
    return getHoverText(hovers).includes("BrowserCreated");
  }, "csls did not load a file created in VS Code.");
  const legend = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokensLegend | undefined>(
      "vscode.provideDocumentSemanticTokensLegend",
      uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token legend for a created file did not complete.",
  );
  const tokens = await withTimeout(
    vscode.commands.executeCommand<vscode.SemanticTokens | undefined>(
      "vscode.provideDocumentSemanticTokens",
      uri,
    ),
    languageFeatureTimeoutMilliseconds,
    "The VS Code semantic-token provider for a created file did not complete.",
  );
  assert(
    legend !== undefined && tokens !== undefined,
    "csls must return semantic tokens for a file created in VS Code.",
  );
  assertAllSemanticTokenTypes(document, source, tokens, legend, "Lazy", "class");
  assertAllSemanticTokenTypes(document, source, tokens, legend, "Func", "type");
  assertAllSemanticTokenTypes(
    document,
    source,
    tokens,
    legend,
    "ConstructorInfo",
    "class",
  );
  assertAllSemanticTokenTypes(
    document,
    source,
    tokens,
    legend,
    "MethodInfo",
    "class",
  );
  await vscode.workspace.fs.delete(uri);
}

function assertAllSemanticTokenTypes(
  document: vscode.TextDocument,
  source: string,
  tokens: vscode.SemanticTokens,
  legend: vscode.SemanticTokensLegend,
  text: string,
  expectedType: string,
): void {
  const tokenTypes = new Map<string, string>();
  let line = 0;
  let start = 0;
  for (let index = 0; index < tokens.data.length; index += 5) {
    const deltaLine = tokens.data[index]!;
    line += deltaLine;
    const deltaStart = tokens.data[index + 1]!;
    start = deltaLine === 0 ? start + deltaStart : deltaStart;
    tokenTypes.set(
      `${line}:${start}:${tokens.data[index + 2]!}`,
      legend.tokenTypes[tokens.data[index + 3]!]!,
    );
  }

  let offset = 0;
  let occurrenceCount = 0;
  while ((offset = source.indexOf(text, offset)) >= 0) {
    const position = document.positionAt(offset);
    assert(
      tokenTypes.get(`${position.line}:${position.character}:${text.length}`) === expectedType,
      `${text} at ${position.line + 1}:${position.character + 1} must have semantic token ${expectedType}.`,
    );
    occurrenceCount++;
    offset += text.length;
  }

  assert(occurrenceCount > 0, `${text} was not found in the created VS Code document.`);
}

async function assertConsoleHover(documentUri: vscode.Uri): Promise<void> {
  const deadline = Date.now() + languageFeatureTimeoutMilliseconds;
  while (Date.now() < deadline) {
    const hovers = await withTimeout(
      vscode.commands.executeCommand<readonly vscode.Hover[]>(
        "vscode.executeHoverProvider",
        documentUri,
        new vscode.Position(0, 2),
      ),
      deadline - Date.now(),
      "The VS Code hover provider did not complete.",
    );
    const hoverText = getHoverText(hovers);
    if (/System\.Console/u.test(hoverText)) {
      return;
    }

    await delay(100);
  }

  throw new Error("csls did not return the System.Console hover in VS Code.");
}

async function replaceDocumentText(
  document: vscode.TextDocument,
  text: string,
): Promise<void> {
  const edit = new vscode.WorkspaceEdit();
  edit.replace(
    document.uri,
    new vscode.Range(document.positionAt(0), document.positionAt(document.getText().length)),
    text,
  );
  assert(
    await vscode.workspace.applyEdit(edit),
    `VS Code could not edit ${document.uri.toString()}.`,
  );
}

async function waitUntil(
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

function getHoverText(hovers: readonly vscode.Hover[] | undefined): string {
  return hovers
    ?.flatMap((hover) => hover.contents)
    .map((content) => typeof content === "string" ? content : content.value)
    .join("\n") ?? "";
}

function getCompletionLabel(item: vscode.CompletionItem): string {
  return typeof item.label === "string" ? item.label : item.label.label;
}

function getInlayHintLabel(hint: vscode.InlayHint): string {
  return typeof hint.label === "string"
    ? hint.label
    : hint.label.map((part) => part.value).join("");
}

function isExtensionApi(value: unknown): value is {
  readonly host: string;
  readonly projects?: () => readonly {
    readonly name: string;
    readonly path: string;
  }[];
  readonly runtimePath?: string;
  readonly sdkPath?: string;
  readonly serverPath?: string;
  readonly state: number;
  readonly testErrors?: () => readonly string[];
  readonly tests?: () => readonly string[];
} {
  return typeof value === "object" &&
    value !== null &&
    "host" in value &&
    typeof value.host === "string" &&
    "state" in value &&
    typeof value.state === "number";
}

function assert(condition: boolean, message: string): asserts condition {
  if (!condition) {
    throw new Error(message);
  }
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function withTimeout<T>(
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
