import { runFeatureContract } from "./web-suite";

export async function run(): Promise<void> {
  const expectedServerPath = requireEnvironment("CSLS_VSCODE_EXPECTED_SERVER_PATH");
  const expectedHost = process.env.CSLS_VSCODE_EXPECTED_HOST === "remote"
    ? "remote"
    : "desktop";
  await runFeatureContract({
    expectedHost,
    expectedServerPath,
    requireRuntimeExtension: true,
  });
}

function requireEnvironment(name: string): string {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
