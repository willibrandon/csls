import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import * as vscode from "vscode";
import { DotnetProjectInspector } from "./dotnetProjectInspector.js";
import type { MtpTestListDocument } from "./mtpTestListDocument.js";
import { ProcessExecutor } from "./processExecutor.js";
import type { TestItemMetadata } from "./testItemMetadata.js";
import { TrxTestResultParser } from "./trxTestResultParser.js";

export class TestExplorer implements vscode.Disposable {
  private readonly controller: vscode.TestController;
  private readonly disposables: vscode.Disposable[];
  private readonly executor: ProcessExecutor;
  private readonly inspector: DotnetProjectInspector;
  private readonly metadata = new Map<string, TestItemMetadata>();
  private readonly parser = new TrxTestResultParser();
  private operationPromise: Promise<void> = Promise.resolve();
  private refreshPromise: Promise<void> | undefined;
  private refreshTimer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    dotnetPath: string,
    private readonly outputChannel: vscode.LogOutputChannel,
    private readonly getProjects: () => readonly {
      readonly name: string;
      readonly path: string;
    }[],
  ) {
    this.executor = new ProcessExecutor(dotnetPath, outputChannel);
    this.inspector = new DotnetProjectInspector(this.executor);
    this.controller = vscode.tests.createTestController("csls", "csls");
    this.controller.refreshHandler = (token) => this.refresh(token);
    this.controller.resolveHandler = () => this.refresh();
    this.disposables = [
      this.controller,
      this.controller.createRunProfile(
        "Run",
        vscode.TestRunProfileKind.Run,
        async (request, token) => {
          await this.execute(request, token);
        },
        true,
      ),
    ];
    const watcher = vscode.workspace.createFileSystemWatcher(
      "**/{*.cs,*.csproj,*.props,*.targets,global.json}",
    );
    this.disposables.push(
      watcher,
      watcher.onDidChange(() => this.scheduleRefresh()),
      watcher.onDidCreate(() => this.scheduleRefresh()),
      watcher.onDidDelete(() => this.scheduleRefresh()),
    );
  }

  async refresh(cancellationToken?: vscode.CancellationToken): Promise<void> {
    if (this.refreshPromise !== undefined) {
      return this.refreshPromise;
    }

    this.refreshPromise = this.enqueueOperation(
      () => this.refreshCore(cancellationToken),
    ).finally(() => {
      this.refreshPromise = undefined;
    });
    return this.refreshPromise;
  }

  refreshInBackground(): void {
    void this.refresh().catch((error: unknown) => {
      const message = error instanceof Error ? error.message : String(error);
      this.outputChannel.error(`Test discovery failed: ${message}`);
    });
  }

  async run(projectPath?: string): Promise<void> {
    await this.refresh();
    const errors = this.getErrors();
    if (errors.length > 0) {
      throw new Error(errors[0]);
    }

    const include: vscode.TestItem[] = [];
    this.controller.items.forEach((item) => {
      if (projectPath === undefined || item.uri?.fsPath === projectPath) {
        include.push(item);
      }
    });
    if (include.length === 0) {
      throw new Error("No Microsoft Testing Platform projects were discovered.");
    }

    const cancellation = new vscode.CancellationTokenSource();
    try {
      const failed = await this.execute(
        new vscode.TestRunRequest(include),
        cancellation.token,
      );
      if (failed > 0) {
        throw new Error(`${failed} test${failed === 1 ? "" : "s"} failed.`);
      }
    } finally {
      cancellation.dispose();
    }
  }

  getTestNames(): readonly string[] {
    const names: string[] = [];
    for (const itemId of this.metadata.keys()) {
      const item = findTestItem(this.controller.items, itemId);
      if (item !== undefined) {
        names.push(item.label);
      }
    }

    return names;
  }

  getErrors(): readonly string[] {
    const errors: string[] = [];
    const visit = (item: vscode.TestItem): void => {
      if (typeof item.error === "string") {
        errors.push(item.error);
      } else if (item.error !== undefined) {
        errors.push(item.error.value);
      }

      item.children.forEach(visit);
    };
    this.controller.items.forEach(visit);
    return errors;
  }

  dispose(): void {
    if (this.refreshTimer !== undefined) {
      clearTimeout(this.refreshTimer);
      this.refreshTimer = undefined;
    }

    for (const disposable of this.disposables.reverse()) {
      disposable.dispose();
    }
  }

  private async refreshCore(cancellationToken?: vscode.CancellationToken): Promise<void> {
    const items: vscode.TestItem[] = [];
    const metadata = new Map<string, TestItemMetadata>();
    for (const project of this.getProjects()) {
      if (cancellationToken?.isCancellationRequested === true) {
        break;
      }

      try {
        const item = await this.discoverProject(project, metadata, cancellationToken);
        if (item !== undefined) {
          items.push(item);
        }
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        this.outputChannel.error(`Test discovery failed for ${project.path}: ${message}`);
        const item = this.createProjectItem(project);
        item.error = message;
        items.push(item);
      }
    }

    if (cancellationToken?.isCancellationRequested !== true) {
      this.metadata.clear();
      for (const [itemId, value] of metadata) {
        this.metadata.set(itemId, value);
      }

      this.controller.items.replace(items);
      this.controller.invalidateTestResults();
    }
  }

  private async discoverProject(
    project: { readonly name: string; readonly path: string },
    metadata: Map<string, TestItemMetadata>,
    cancellationToken?: vscode.CancellationToken,
  ): Promise<vscode.TestItem | undefined> {
    const projectDirectory = dirname(project.path);
    const properties = await this.inspector.inspect(project.path, cancellationToken);
    if (properties.IsTestingPlatformApplication !== "true" ||
      properties.OutputType !== "Exe" ||
      properties.TargetPath === undefined ||
      properties.TargetPath.length === 0) {
      return undefined;
    }

    const projectItem = this.createProjectItem(project);

    const build = await this.executor.execute(
      ["build", project.path, "--nologo"],
      projectDirectory,
      cancellationToken,
    );
    if (build.exitCode !== 0 || cancellationToken?.isCancellationRequested) {
      if (cancellationToken?.isCancellationRequested !== true) {
        projectItem.error = describeFailure("Test discovery build failed.", build);
      }

      return projectItem;
    }

    const discovery = await this.executor.execute(
      [properties.TargetPath, "--list-tests", "json", "--no-ansi", "--progress", "off"],
      projectDirectory,
      cancellationToken,
    );
    if (discovery.exitCode !== 0 || cancellationToken?.isCancellationRequested === true) {
      if (cancellationToken?.isCancellationRequested !== true) {
        projectItem.error = describeFailure("Microsoft Testing Platform discovery failed.", discovery);
      }

      return projectItem;
    }

    const document = parseJson<MtpTestListDocument>(discovery.stdout);
    if (document.schemaVersion !== 1) {
      throw new Error(`Unsupported Microsoft Testing Platform test-list schema ${document.schemaVersion}.`);
    }

    const classes = new Map<string, vscode.TestItem>();
    for (const test of document.tests) {
      const className = getClassName(test.type);
      let classItem = classes.get(className);
      if (classItem === undefined) {
        classItem = this.controller.createTestItem(
          `class:${project.path}:${className}`,
          className,
          test.location?.file === undefined ? undefined : vscode.Uri.file(test.location.file),
        );
        classes.set(className, classItem);
        projectItem.children.add(classItem);
      }

      const itemId = `test:${project.path}:${test.uid}`;
      const uri = test.location?.file === undefined
        ? undefined
        : vscode.Uri.file(test.location.file);
      const item = this.controller.createTestItem(itemId, test.displayName, uri);
      if (test.location?.lineStart !== undefined) {
        const start = Math.max(0, test.location.lineStart - 1);
        const end = Math.max(start, (test.location.lineEnd ?? test.location.lineStart) - 1);
        item.range = new vscode.Range(start, 0, end, Number.MAX_SAFE_INTEGER);
      }

      classItem.children.add(item);
      metadata.set(itemId, {
        projectPath: project.path,
        targetPath: properties.TargetPath,
        uid: test.uid,
      });
    }

    return projectItem;
  }

  private createProjectItem(
    project: { readonly name: string; readonly path: string },
  ): vscode.TestItem {
    return this.controller.createTestItem(
      `project:${project.path}`,
      project.name,
      vscode.Uri.file(project.path),
    );
  }

  private execute(
    request: vscode.TestRunRequest,
    cancellationToken: vscode.CancellationToken,
  ): Promise<number> {
    return this.enqueueOperation(() => this.executeCore(request, cancellationToken));
  }

  private async executeCore(
    request: vscode.TestRunRequest,
    cancellationToken: vscode.CancellationToken,
  ): Promise<number> {
    const run = this.controller.createTestRun(request);
    const selected = this.selectTests(request);
    for (const item of selected) {
      run.enqueued(item);
    }

    let failed = 0;
    try {
      const groups = Map.groupBy(selected, (item) => {
        const metadata = this.metadata.get(item.id);
        return `${metadata?.projectPath ?? ""}\0${metadata?.targetPath ?? ""}`;
      });
      for (const items of groups.values()) {
        if (cancellationToken.isCancellationRequested) {
          break;
        }

        failed += await this.executeGroup(items, run, cancellationToken);
      }
    } finally {
      run.end();
    }

    return failed;
  }

  private async executeGroup(
    items: readonly vscode.TestItem[],
    run: vscode.TestRun,
    cancellationToken: vscode.CancellationToken,
  ): Promise<number> {
    const first = this.metadata.get(items[0]?.id ?? "");
    if (first === undefined) {
      return 0;
    }

    for (const item of items) {
      run.started(item);
    }

    const build = await this.executor.execute(
      ["build", first.projectPath, "--nologo"],
      dirname(first.projectPath),
      cancellationToken,
    );
    appendOutput(run, build.stdout, build.stderr);
    if (build.exitCode !== 0) {
      const message = new vscode.TestMessage("The test project did not build.");
      for (const item of items) {
        run.errored(item, message);
      }

      return items.length;
    }

    const resultsDirectory = await mkdtemp(join(tmpdir(), "csls-tests-"));
    const reportName = "results.trx";
    try {
      const arguments_ = [
        first.targetPath,
        "--filter-uid",
        ...items.map((item) => this.metadata.get(item.id)?.uid ?? ""),
        "--report-trx",
        "--report-trx-filename",
        reportName,
        "--results-directory",
        resultsDirectory,
        "--no-ansi",
        "--progress",
        "off",
        "--output",
        "Detailed",
      ];
      const execution = await this.executor.execute(
        arguments_,
        dirname(first.projectPath),
        cancellationToken,
        true,
      );
      appendOutput(run, execution.stdout, execution.stderr);
      if (cancellationToken.isCancellationRequested) {
        return 0;
      }

      const results = await this.parser.parse(join(resultsDirectory, reportName));
      let failed = 0;
      for (const item of items) {
        const metadata = this.metadata.get(item.id);
        const result = metadata === undefined ? undefined : results.get(metadata.uid);
        if (result === undefined) {
          run.errored(item, new vscode.TestMessage("Microsoft Testing Platform returned no result."));
          failed++;
        } else if (result.outcome === "Passed") {
          run.passed(item, result.durationMilliseconds);
        } else if (result.outcome === "NotExecuted") {
          run.skipped(item);
        } else {
          run.failed(
            item,
            new vscode.TestMessage(`${result.testName} finished with outcome ${result.outcome}.`),
            result.durationMilliseconds,
          );
          failed++;
        }
      }

      return failed;
    } finally {
      await rm(resultsDirectory, { force: true, recursive: true });
    }
  }

  private selectTests(request: vscode.TestRunRequest): vscode.TestItem[] {
    const selected: vscode.TestItem[] = [];
    const excluded = new Set(request.exclude?.map((item) => item.id));
    const visit = (item: vscode.TestItem): void => {
      if (excluded.has(item.id)) {
        return;
      }

      if (this.metadata.has(item.id)) {
        selected.push(item);
      }

      item.children.forEach(visit);
    };
    if (request.include === undefined) {
      this.controller.items.forEach(visit);
    } else {
      for (const item of request.include) {
        visit(item);
      }
    }

    return selected;
  }

  private enqueueOperation<TResult>(operation: () => Promise<TResult>): Promise<TResult> {
    const result = this.operationPromise.then(operation, operation);
    this.operationPromise = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  private scheduleRefresh(): void {
    this.controller.invalidateTestResults();
    if (this.refreshTimer !== undefined) {
      clearTimeout(this.refreshTimer);
    }

    this.refreshTimer = setTimeout(() => {
      this.refreshTimer = undefined;
      this.refreshInBackground();
    }, 250);
  }
}

function appendOutput(run: vscode.TestRun, stdout: string, stderr: string): void {
  const output = `${stdout}${stderr}`.replace(/(?<!\r)\n/gu, "\r\n");
  if (output.length > 0) {
    run.appendOutput(output);
  }
}

function findTestItem(
  collection: vscode.TestItemCollection,
  itemId: string,
): vscode.TestItem | undefined {
  let found: vscode.TestItem | undefined;
  collection.forEach((item) => {
    if (found !== undefined) {
      return;
    }

    found = item.id === itemId ? item : findTestItem(item.children, itemId);
  });
  return found;
}

function getClassName(type: MtpTestListDocument["tests"][number]["type"]): string {
  if (type?.typeName === undefined) {
    return "Tests";
  }

  return type.namespace === undefined || type.namespace.length === 0
    ? type.typeName
    : `${type.namespace}.${type.typeName}`;
}

function parseJson<T>(text: string): T {
  const start = text.indexOf("{");
  const end = text.lastIndexOf("}");
  if (start < 0 || end < start) {
    throw new Error("The dotnet command did not return a JSON document.");
  }

  return JSON.parse(text.slice(start, end + 1)) as T;
}

function describeFailure(
  summary: string,
  result: { readonly stderr: string; readonly stdout: string },
): string {
  const output = `${result.stdout}\n${result.stderr}`.trim();
  const maximumOutputLength = 8_192;
  const boundedOutput = output.length <= maximumOutputLength
    ? output
    : `${output.slice(0, maximumOutputLength)}\nOutput truncated.`;
  return boundedOutput.length === 0 ? summary : `${summary}\n\n${boundedOutput}`;
}
