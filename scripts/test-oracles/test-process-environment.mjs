export function testProcessEnvironment(environment = process.env) {
  const result = { ...environment };
  // Fixture helpers may emit workflow commands; only explicit test-owned files
  // may receive them, never the enclosing Actions step's command files.
  for (const name of ["GITHUB_OUTPUT", "GITHUB_ENV", "GITHUB_PATH", "GITHUB_STEP_SUMMARY", "GITHUB_STATE"]) {
    delete result[name];
  }
  return result;
}
