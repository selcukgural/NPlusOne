using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Runs in process: the analyzer benchmarks compile against the assemblies this process loads, and an out-of-process
// run would build a generated project under the repository's warnings-as-errors and locked-restore settings.
// dotnet run -c Release --project benchmarks/NPlusOne.Benchmarks -- --filter '*Fingerprint*'
var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance))
    .AddDiagnoser(MemoryDiagnoser.Default);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
