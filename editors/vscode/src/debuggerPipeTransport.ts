import * as vscode from "vscode";

const debuggerCommandToken = "${debuggerCommand}";
const maximumArguments = 64;
const maximumEnvironmentEntries = 128;

interface PipeTransport {
  readonly pipeProgram: string;
  readonly pipeArgs: readonly string[];
  readonly debuggerPath: string;
  readonly debuggerRuntimePath?: string;
  readonly pipeCwd?: string;
  readonly pipeEnv?: Readonly<Record<string, string>>;
  readonly commandShell: "posix" | "powershell";
}

/** Creates the actual editor-to-adapter pipe process from a resolved debug configuration. */
export function createPipeTransportExecutable(value: unknown): vscode.DebugAdapterExecutable {
  const transport = parsePipeTransport(value);
  const managedLauncher = transport.debuggerPath.toLowerCase().endsWith(".dll");
  const executable = managedLauncher
    ? transport.debuggerRuntimePath ?? "dotnet"
    : transport.debuggerPath;
  const commandArguments = managedLauncher
    ? [transport.debuggerPath, "debugger", "dap"]
    : ["debugger", "dap"];
  const command = transport.commandShell === "powershell"
    ? `& ${[executable, ...commandArguments].map(quotePowerShell).join(" ")}`
    : [executable, ...commandArguments].map(quotePosix).join(" ");
  let substitutions = 0;
  const args = transport.pipeArgs.map((argument) => {
    if (argument === debuggerCommandToken) {
      substitutions++;
      return command;
    }
    if (argument.includes(debuggerCommandToken)) {
      throw new Error("pipeTransport.pipeArgs must use ${debuggerCommand} as a whole argument.");
    }
    return argument;
  });
  if (substitutions > 1) {
    throw new Error("pipeTransport.pipeArgs may contain ${debuggerCommand} only once.");
  }
  if (substitutions === 0) {
    args.push(command);
  }
  return new vscode.DebugAdapterExecutable(transport.pipeProgram, args, {
    ...(transport.pipeCwd === undefined ? {} : { cwd: transport.pipeCwd }),
    ...(transport.pipeEnv === undefined ? {} : { env: { ...transport.pipeEnv } }),
  });
}

function parsePipeTransport(value: unknown): PipeTransport {
  const object = requireObject(value, "pipeTransport");
  const allowed = new Set([
    "pipeProgram", "pipeArgs", "debuggerPath", "debuggerRuntimePath", "pipeCwd", "pipeEnv", "commandShell",
  ]);
  for (const key of Object.keys(object)) {
    if (!allowed.has(key)) {
      throw new Error(`pipeTransport.${key} is not a supported property.`);
    }
  }

  const pipeProgram = requireText(object["pipeProgram"], "pipeTransport.pipeProgram");
  const debuggerPath = requireText(object["debuggerPath"], "pipeTransport.debuggerPath");
  const debuggerRuntimePath = optionalText(object["debuggerRuntimePath"], "pipeTransport.debuggerRuntimePath");
  if (debuggerRuntimePath !== undefined && !debuggerPath.toLowerCase().endsWith(".dll")) {
    throw new Error("pipeTransport.debuggerRuntimePath applies only to a .dll debuggerPath.");
  }
  const pipeCwd = optionalText(object["pipeCwd"], "pipeTransport.pipeCwd");
  const shell = object["commandShell"] ?? "posix";
  if (shell !== "posix" && shell !== "powershell") {
    throw new Error("pipeTransport.commandShell must be posix or powershell.");
  }

  const rawArgs = object["pipeArgs"] ?? [];
  if (!Array.isArray(rawArgs) || rawArgs.length > maximumArguments) {
    throw new Error(`pipeTransport.pipeArgs must be an array of at most ${maximumArguments} strings.`);
  }
  const pipeArgs = rawArgs.map((argument: unknown) => requireArgument(argument, "pipeTransport.pipeArgs"));
  const rawEnv = object["pipeEnv"];
  let pipeEnv: Record<string, string> | undefined;
  if (rawEnv !== undefined) {
    const environment = requireObject(rawEnv, "pipeTransport.pipeEnv");
    const entries = Object.entries(environment);
    if (entries.length > maximumEnvironmentEntries) {
      throw new Error(`pipeTransport.pipeEnv exceeds ${maximumEnvironmentEntries} entries.`);
    }
    pipeEnv = Object.create(null) as Record<string, string>;
    for (const [name, rawValue] of entries) {
      if (name.length === 0 || name.includes("=") || hasControlCharacter(name)) {
        throw new Error("pipeTransport.pipeEnv contains an invalid variable name.");
      }
      pipeEnv[name] = requireArgument(rawValue, `pipeTransport.pipeEnv.${name}`);
    }
  }

  return {
    pipeProgram, pipeArgs, debuggerPath, commandShell: shell,
    ...(debuggerRuntimePath === undefined ? {} : { debuggerRuntimePath }),
    ...(pipeCwd === undefined ? {} : { pipeCwd }),
    ...(pipeEnv === undefined ? {} : { pipeEnv }),
  };
}

function requireObject(value: unknown, name: string): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function requireText(value: unknown, name: string): string {
  const result = requireArgument(value, name);
  if (result.trim().length === 0) {
    throw new Error(`${name} must be a nonempty string.`);
  }
  return result;
}

function optionalText(value: unknown, name: string): string | undefined {
  return value === undefined ? undefined : requireText(value, name);
}

function requireArgument(value: unknown, name: string): string {
  if (typeof value !== "string" || value.length > 32768 || hasControlCharacter(value)) {
    throw new Error(`${name} must be a string without control characters of at most 32768 characters.`);
  }
  return value;
}

function hasControlCharacter(value: string): boolean {
  return /[\x00-\x1f\x7f]/u.test(value);
}

function quotePosix(value: string): string {
  return `'${value.replaceAll("'", `'\\''`)}'`;
}

function quotePowerShell(value: string): string {
  return `'${value.replaceAll("'", "''")}'`;
}
