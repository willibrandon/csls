import { readFile } from "node:fs/promises";
import { XMLParser } from "fast-xml-parser";
import type { TrxTestResult } from "./trxTestResult.js";

export class TrxTestResultParser {
  async parse(path: string): Promise<ReadonlyMap<string, TrxTestResult>> {
    const xml = await readFile(path, "utf8");
    const results = new Map<string, TrxTestResult>();
    const parser = new XMLParser({
      attributeNamePrefix: "",
      ignoreAttributes: false,
      isArray: (_name, path) => path === "TestRun.Results.UnitTestResult",
    });
    const document = parser.parse(xml) as {
      readonly TestRun?: {
        readonly Results?: {
          readonly UnitTestResult?: readonly Record<string, unknown>[];
        };
      };
    };
    for (const result of document.TestRun?.Results?.UnitTestResult ?? []) {
      const testId = getString(result, "testId");
      const outcome = getString(result, "outcome");
      const testName = getString(result, "testName");
      if (testId !== undefined && outcome !== undefined && testName !== undefined) {
        results.set(testId, {
          durationMilliseconds: parseDuration(getString(result, "duration")),
          outcome,
          testId,
          testName,
        });
      }
    }

    return results;
  }
}

function getString(
  values: Readonly<Record<string, unknown>>,
  name: string,
): string | undefined {
  const value = values[name];
  return typeof value === "string" ? value : undefined;
}

function parseDuration(value: string | undefined): number | undefined {
  if (value === undefined) {
    return undefined;
  }

  const match = /^(\d+):(\d+):(\d+)(?:\.(\d+))?$/u.exec(value);
  if (match === null) {
    return undefined;
  }

  const fraction = (match[4] ?? "").padEnd(3, "0").slice(0, 3);
  return (((Number(match[1]) * 60 + Number(match[2])) * 60 + Number(match[3])) * 1_000) +
    Number(fraction);
}
