const { writeFile } = require("node:fs/promises");

exports.activate = async function activate() {
  const resultPath = process.env.CSLS_VSCODE_REMOTE_RESULT_PATH;
  if (resultPath === undefined || resultPath.length === 0) {
    return;
  }

  let result;
  try {
    const suitePath = process.env.CSLS_VSCODE_REMOTE_SUITE ?? "./dist/suite.cjs";
    if (!["./dist/suite.cjs", "./dist/startup-suite.cjs"].includes(suitePath)) {
      throw new Error(`Unsupported remote VS Code test suite: ${suitePath}`);
    }

    await require(suitePath).run();
    result = { success: true };
  } catch (error) {
    result = {
      success: false,
      error: error instanceof Error ? error.stack ?? error.message : String(error),
    };
  }

  await writeFile(resultPath, JSON.stringify(result), "utf8");
};
