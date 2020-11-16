using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace NetworkToolkit.Benchmarks
{
    public class NetworkToolkitConfig : ManualConfig
    {
        public NetworkToolkitConfig()
        {
            AddDiagnoser(MemoryDiagnoser.Default);
            AddJob(Job.Default
                .WithGcServer(true)
                .WithEnvironmentVariable("DOTNET_SYSTEM_THREADING_POOLASYNCVALUETASKS", "1"));
        }
    }
}
