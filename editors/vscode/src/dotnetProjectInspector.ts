import { dirname } from "node:path";
import * as vscode from "vscode";
import type { MsbuildProjectPropertiesDocument } from "./msbuildProjectPropertiesDocument.js";
import { ProcessExecutor } from "./processExecutor.js";

export class DotnetProjectInspector {
  constructor(private readonly executor: ProcessExecutor) {}

  async inspect(
    projectPath: string,
    cancellationToken?: vscode.CancellationToken,
    globalProperties: Readonly<Record<string, string>> = {},
  ): Promise<MsbuildProjectPropertiesDocument["Properties"]> {
    const evaluation = await this.executor.execute(
      [
        "msbuild",
        projectPath,
        "-getProperty:TargetPath,IsTestingPlatformApplication,TargetFramework,OutputType",
        "-maxcpucount:1",
        "-nologo",
        "-nodeReuse:false",
        "-verbosity:quiet",
        ...Object.entries(globalProperties).map(([name, value]) => `-property:${name}=${value}`),
      ],
      dirname(projectPath),
      cancellationToken,
      false,
      false,
    );
    if (evaluation.exitCode !== 0) {
      throw new Error(
        `dotnet msbuild failed with exit code ${evaluation.exitCode ?? "unknown"}.`,
      );
    }

    const start = evaluation.stdout.indexOf("{");
    const end = evaluation.stdout.lastIndexOf("}");
    if (start < 0 || end < start) {
      throw new Error("dotnet msbuild did not return a JSON document.");
    }

    return (JSON.parse(
      evaluation.stdout.slice(start, end + 1),
    ) as MsbuildProjectPropertiesDocument).Properties;
  }
}
