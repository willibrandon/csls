const { readFile } = require("node:fs/promises");

exports.run = async function run() {
  const resultPath = requireEnvironment("CSLS_VSCODE_REMOTE_RESULT_PATH");
  const deadline = Date.now() + 300_000;
  while (Date.now() < deadline) {
    try {
      const result = JSON.parse(await readFile(resultPath, "utf8"));
      if (result.success === true) {
        return;
      }

      throw new Error(result.error ?? "The remote VS Code feature suite failed.");
    } catch (error) {
      if (error?.code !== "ENOENT") {
        throw error;
      }
    }

    await new Promise((resolve) => setTimeout(resolve, 100));
  }

  throw new Error("The remote VS Code feature suite did not finish within 300 seconds.");
};

function requireEnvironment(name) {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(name + " is required.");
  }

  return value;
}
