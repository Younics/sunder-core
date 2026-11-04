export function dotnetTestBudget(project, selected) {
  const integration = selected.some((test) => test.project === project && test.primaryLane === "host-platform");
  const packageBuild = project === "Sunder.Package.Build.Tests";
  const executionBuild = project === "Sunder.ExecutionRuntime.Build.Tests";
  const templates = project === "Sunder.Package.Templates.Tests";
  return {
    // The measured win-x64 Build invocation took 66.5 minutes with 87/87 passing.
    // Scope the extra project budget to its process/build selection, not fast tests.
    timeoutMilliseconds: packageBuild && integration ? 90 * 60_000 : 60 * 60_000,
    hangTimeout: integration && (packageBuild || templates) ? "20m"
      : integration && executionBuild ? "15m" : templates ? "10m" : "5m",
    diagnostics: packageBuild || integration && (templates || executionBuild),
    crashDump: packageBuild,
  };
}

export function dotnetTestArguments({ projectPath, projectName, outputDirectory, logStem, build, filter, budget, extraArguments,
  diagnostics = process.env.SUNDER_TEST_DIAGNOSTICS === "1" }) {
  return ["test", projectPath, "--nologo", "--no-restore", ...(!build ? ["--no-build"] : []),
    "--blame-hang", "--blame-hang-timeout", budget.hangTimeout, "--blame-hang-dump-type", "none",
    ...(diagnostics && budget.diagnostics ? ["--diag", `${logStem}.vstest.log`] : []),
    ...(diagnostics && budget.crashDump ? ["--blame-crash", "--blame-crash-dump-type", "mini"] : []),
    "--logger", `trx;LogFileName=${projectName}.trx`, "--results-directory", outputDirectory,
    ...(filter === undefined ? [] : ["--filter", filter]), ...extraArguments];
}
