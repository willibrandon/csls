import { strict as assert } from "node:assert";
import { type Locator } from "playwright-core";
import { DebuggerWorkbenchUi } from "./debugger-workbench-ui";

/** Drives only the real Electron workbench DOM through its loopback debugging endpoint. */
export class ResultsViewUi {
  private constructor(private readonly workbench: DebuggerWorkbenchUi, private readonly tree: Locator) {}

  static async connect(timeout: number): Promise<ResultsViewUi> {
    const workbench = await DebuggerWorkbenchUi.connect(timeout);
    try {
      const tree = workbench.page.getByRole("tree", { name: "Debug Variables", exact: true });
      await tree.waitFor({ state: "visible" });
      return new ResultsViewUi(workbench, tree);
    } catch (error) {
      await workbench.dispose();
      throw error;
    }
  }

  async expandLocals(): Promise<void> {
    await this.expandRow("Scope Locals");
    await this.row(/^values, value /).waitFor({ state: "visible" });
  }

  async expandEnumerable(): Promise<void> {
    await this.expandRow(/^values, value /);
    await this.row(/^Results View, value /).waitFor({ state: "visible" });
  }

  async collapseEnumerable(): Promise<void> {
    const row = this.row(/^values, value /);
    await row.scrollIntoViewIfNeeded();
    if (await row.getAttribute("aria-expanded") === "true") {
      await row.locator(".monaco-tl-twistie").click();
    }

    assert.equal(await row.getAttribute("aria-expanded"), "false",
      "The snapshot rediscovery check must start from a collapsed enumerable.");
  }

  async resolveResultsView(): Promise<void> {
    await this.row(/^Results View, value /).locator(".lazy-button").click();
  }

  async expandedScopeNames(): Promise<readonly string[]> {
    return this.tree.locator('[role="treeitem"][aria-level="1"][aria-expanded="true"]')
      .evaluateAll((rows) => rows.map((row) => row.getAttribute("aria-label") ?? "")
        .filter((name) => name.startsWith("Scope ")).map((name) => name.slice("Scope ".length)));
  }

  async waitForRenderedProjection(timeout: number): Promise<void> {
    // The caller first awaits the refreshed projection and a protocol dispatch
    // barrier. Observe the subsequent renderer commit before using its rows.
    await this.tree.evaluate((_tree, deadlineMilliseconds) => new Promise<void>((resolve, reject) => {
      const frame = requestAnimationFrame(() => {
        clearTimeout(deadline);
        resolve();
      });
      const deadline = setTimeout(() => {
        cancelAnimationFrame(frame);
        reject(new Error("The Variables view did not commit the refreshed projection."));
      }, deadlineMilliseconds);
    }), timeout);
  }

  async expandSnapshot(): Promise<void> {
    const row = this.row(/^Results View, value /);
    await row.locator(".expression:not(.lazy)").waitFor({ state: "visible" });
    await this.expandRow(/^Results View, value /);
    await this.row(/^\[100\.\.199\], value(?: |$)/).waitFor({ state: "visible" });
  }

  async expandChunk(start: number, end: number): Promise<void> {
    await this.expandRow(new RegExp(`^\\[${start}\\.\\.${end}\\], value(?: |$)`));
    await this.row(new RegExp(`^\\[${start}\\], value ${start}$`)).waitFor({ state: "visible" });
  }

  async collapseChunk(start: number, end: number): Promise<void> {
    const row = this.row(new RegExp(`^\\[${start}\\.\\.${end}\\], value(?: |$)`));
    await row.scrollIntoViewIfNeeded();
    if (await row.getAttribute("aria-expanded") === "true") {
      await row.locator(".monaco-tl-twistie").click();
    }
  }

  async captureDiagnostics(): Promise<string> {
    const sections: string[] = [];
    try {
      sections.push("Variables accessibility tree:\n" +
        (await this.tree.ariaSnapshot({ timeout: 2_000, depth: 8 })).slice(0, 16_384));
    } catch (error) {
      sections.push(`Variables accessibility snapshot failed: ${String(error)}`);
    }

    try {
      sections.push("Variables rendered DOM:\n" +
        (await this.tree.innerHTML({ timeout: 2_000 })).slice(0, 16_384));
    } catch (error) {
      sections.push(`Variables DOM snapshot failed: ${String(error)}`);
    }

    return sections.join("\n");
  }

  async dispose(): Promise<void> {
    // For connectOverCDP, Playwright closes its transport, not the Electron process.
    // The existing extension-test runner remains the owner of the isolated editor.
    await this.workbench.dispose();
  }

  private row(name: string | RegExp): Locator {
    return this.tree.getByRole("treeitem", { name, exact: typeof name === "string" });
  }

  private async expandRow(name: string | RegExp): Promise<void> {
    const row = this.row(name);
    await row.waitFor({ state: "visible" });
    await row.scrollIntoViewIfNeeded();
    if (await row.getAttribute("aria-expanded") !== "true") {
      await row.locator(".monaco-tl-twistie").click();
    }
  }
}
