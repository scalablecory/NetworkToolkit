using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NetworkToolkit.Tests
{
    public class TestsBase
    {
        public static async Task RunClientServer(Func<Task> clientFunc, Func<Task> serverFunc, int? millisecondsTimeout = null)
        {
            Task[] tasks = new[]
            {
                Task.Run(() => clientFunc()),
                Task.Run(() => serverFunc())
            };

            if (Debugger.IsAttached)
            {
                await tasks.WhenAllOrAnyFailed().ConfigureAwait(false);
            }
            else
            {
                await tasks.WhenAllOrAnyFailed(millisecondsTimeout ?? 1_000).ConfigureAwait(false);
            }
        }
    }
}
