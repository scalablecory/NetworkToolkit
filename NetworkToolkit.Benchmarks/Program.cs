using BenchmarkDotNet.Running;
using System;

namespace NetworkToolkit.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run(typeof(Program).Assembly);
        }
    }
}
