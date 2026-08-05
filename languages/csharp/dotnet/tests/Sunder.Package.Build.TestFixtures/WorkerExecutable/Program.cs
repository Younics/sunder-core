using Sunder.Sdk.Packaging;
using Sunder.Sdk.Worker;

[assembly: SunderPackage(Id = "test.worker.executable", Name = "Worker Executable Fixture")]

await SunderWorker.RunAsync(new SunderWorkerOptions([]));
