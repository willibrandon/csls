const { writeFile } = require("node:fs/promises");

exports.activate = async function activate() {
  const resultPath = process.env.CSLS_VSCODE_REMOTE_RESULT_PATH;
  if (resultPath === undefined || resultPath.length === 0) {
    return;
  }

  let result;
  try {
    await require("./dist/suite.cjs").run();
    result = { success: true };
  } catch (error) {
    result = {
      success: false,
      error: error instanceof Error ? error.stack ?? error.message : String(error),
    };
  }

  await writeFile(resultPath, JSON.stringify(result), "utf8");
};
