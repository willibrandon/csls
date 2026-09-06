import { strict as assert } from "node:assert";
import { watch } from "node:fs";
import { readFile } from "node:fs/promises";
import { basename, dirname } from "node:path";
import { chromium, type Browser, type Page } from "playwright-core";

/** Owns a connection to the real isolated Electron workbench, leaving its lifetime with the runner. */
export class DebuggerWorkbenchUi {
  private constructor(private readonly browser: Browser, readonly page: Page) {}

  static async connect(timeout: number): Promise<DebuggerWorkbenchUi> {
    const endpointPath = process.env["CSLS_VSCODE_CDP_ENDPOINT_PATH"];
    assert(endpointPath !== undefined, "The isolated runner must publish its ephemeral DevTools endpoint.");
    const endpoint = await readEndpoint(endpointPath, timeout);
    const address = new URL(endpoint);
    assert.equal(address.protocol, "ws:");
    assert.equal(address.hostname, "127.0.0.1");
    const browser = await chromium.connectOverCDP(endpoint, { timeout, noDefaults: true });
    try {
      const pages = browser.contexts().flatMap((context) => context.pages())
        .filter((page) => /\/workbench(?:-dev)?\.html(?:[?#]|$)/.test(page.url()));
      assert.equal(pages.length, 1, "The isolated Electron instance must contain one workbench window.");
      const page = pages[0]!;
      page.setDefaultTimeout(timeout);
      return new DebuggerWorkbenchUi(browser, page);
    } catch (error) {
      await browser.close();
      throw error;
    }
  }

  async dispose(): Promise<void> {
    // Closing a connectOverCDP connection leaves the real editor owned by its test runner.
    await this.browser.close();
  }

  async captureDiagnostics(): Promise<string> {
    try {
      return (await this.page.locator("body").ariaSnapshot({ timeout: 2_000, depth: 12 })).slice(0, 24_000);
    } catch (error) {
      return `Workbench accessibility snapshot failed: ${String(error)}`;
    }
  }
}

async function readEndpoint(path: string, timeout: number): Promise<string> {
  return new Promise<string>((resolve, reject) => {
    const changes = watch(dirname(path), (_event, filename) => {
      if (filename === basename(path)) {
        void inspect();
      }
    });
    const deadline = setTimeout(() => finish(
      new Error("The isolated Electron instance did not publish its DevTools endpoint."),
    ), timeout);
    changes.on("error", finish);
    void inspect();

    async function inspect(): Promise<void> {
      try {
        finish(undefined, await readFile(path, "utf8"));
      } catch (error) {
        if (typeof error !== "object" || error === null || !("code" in error) || error.code !== "ENOENT") {
          finish(error);
        }
      }
    }

    function finish(error?: unknown, endpoint?: string): void {
      clearTimeout(deadline);
      changes.close();
      if (endpoint !== undefined) {
        resolve(endpoint);
      } else {
        reject(error);
      }
    }
  });
}
