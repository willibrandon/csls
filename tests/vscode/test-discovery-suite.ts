import * as vscode from "vscode";
import { assert, step } from "./contract-support";

export async function run(): Promise<void> {
  const workspaceFolder = vscode.workspace.workspaceFolders?.[0];
  assert(workspaceFolder !== undefined, "The test discovery workspace must be open.");
  const extension = vscode.extensions.getExtension("willibrandon.csls");
  assert(extension !== undefined, "The csls extension must be installed.");
  const api = await extension.activate() as {
    refreshTests(): Promise<void>;
    tests(): readonly string[];
    testErrors(): readonly string[];
  };
  const refresh = async (): Promise<void> => {
    await api.refreshTests();
    assert(api.testErrors().length === 0, `Test discovery failed: ${api.testErrors().join("\n")}`);
  };
  await step("Initial discovery", refresh);
  assert(api.tests().includes("RunsFromVsCode"), "Initial discovery must find the fixture test.");
  const marker = vscode.Uri.joinPath(workspaceFolder.uri, "Tests", "discovery-target.txt");
  const readTarget = async (): Promise<string> =>
    new TextDecoder().decode(await vscode.workspace.fs.readFile(marker)).trim();
  const target = await readTarget();
  const assembly = vscode.Uri.file(target);
  const originalStat = await vscode.workspace.fs.stat(assembly);
  await step("Unchanged discovery", refresh);
  assert(await readTarget() === target, "Discovery must retain the same isolated output path.");
  const unchangedStat = await vscode.workspace.fs.stat(assembly);
  assert(unchangedStat.mtime === originalStat.mtime, "Unchanged discovery must not rewrite the test assembly.");

  const source = vscode.Uri.joinPath(workspaceFolder.uri, "Tests", "ExampleTests.cs");
  await vscode.workspace.fs.writeFile(source, new TextEncoder().encode(`
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fixture.Tests;

[TestClass]
public sealed class ExampleTests
{
    public static System.Collections.Generic.IEnumerable<object[]> Cases =>
        [new object[] { global::Calculator.Add(2, 2) }];

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void UpdatedTest(int value) => Assert.AreEqual(4, value);
}
`));
  await step("Changed test discovery", refresh);
  assert(!api.tests().includes("RunsFromVsCode"), "Discovery must remove the old test.");
  assert(api.tests().some((name) => name.includes("UpdatedTest") && name.includes("4")),
    `Discovery must load the changed test assembly: ${JSON.stringify(api.tests())}.`);
  assert(await readTarget() === target, "Changed source must build in the same isolated directory.");
  await vscode.workspace.fs.writeFile(
    vscode.Uri.joinPath(workspaceFolder.uri, "Calculator.cs"),
    new TextEncoder().encode("public static class Calculator { public static int Add(int left, int right) => left * right + 1; }\n"),
  );
  await step("Changed reference discovery", refresh);
  assert(api.tests().some((name) => name.includes("UpdatedTest") && name.includes("5")),
    `Discovery must rebuild referenced projects: ${JSON.stringify(api.tests())}.`);
  assert(!api.tests().some((name) => name.includes("UpdatedTest") && name.includes("4")),
    "Discovery must replace results computed from the previous reference assembly.");
  assert(await readTarget() === target, "Reference changes must retain isolated output ownership.");
  const normalTarget = vscode.Uri.joinPath(
    workspaceFolder.uri, "Tests", "bin", "Debug", "net10.0", "Fixture.Tests.dll",
  );
  let normalOutputExists = false;
  try {
    await vscode.workspace.fs.stat(normalTarget);
    normalOutputExists = true;
  } catch (error) {
    if (!(error instanceof vscode.FileSystemError) || error.code !== "FileNotFound") {
      throw error;
    }
  }
  assert(!normalOutputExists, "Discovery must leave normal workspace build outputs untouched.");
}
