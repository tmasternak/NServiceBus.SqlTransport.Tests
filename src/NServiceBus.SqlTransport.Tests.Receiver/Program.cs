using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Logging;
using NServiceBus.SqlTransport.Tests.Shared;
using Console = System.Console;

namespace NServiceBus.SqlTransport.Tests.Receiver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new EndpointConfiguration(Shared.Configuration.ReceiverEndpointName);

            configuration.UseTransport<SqlServerTransport>().ConnectionString(() => Shared.Configuration.ConnectionString);

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            Console.WriteLine("Press <enter> to exit.");
            Console.ReadLine();
        }
    }

    class TestHandler : IHandleMessages<TestCommand>
    {
        public Task Handle(TestCommand message, IMessageHandlerContext context)
        {
            Statistics.MessageReceived();
            return Task.CompletedTask;
        }
    }

    class ResetHandler : IHandleMessages<ResetCommand>
    {
        public Task Handle(ResetCommand message, IMessageHandlerContext context)
        {
            Statistics.Reset();
            return Task.CompletedTask;
        }
    }

    class Statistics
    {
        const int Interval = 500;
        static int messageCounter;
        static long previousTimestamp;
        static ILog log;

        public static void MessageReceived()
        {
            var newValue = Interlocked.Increment(ref messageCounter);
            if (newValue == 1)
            {
                log = LogManager.GetLogger("Statistics");
                Console.WriteLine("First message received.");
                previousTimestamp = Stopwatch.GetTimestamp();
            }
            else if (newValue % Interval == 0)
            {
                var newTimestamp = Stopwatch.GetTimestamp();
                double elapsed = newTimestamp - previousTimestamp;
                previousTimestamp = newTimestamp;

                var seconds = elapsed / Stopwatch.Frequency;
                var throughput = Interval / seconds;
                var logMessage = $"{DateTime.Now:s};{throughput};{newValue}";
                Console.WriteLine(logMessage);
                log.Info(logMessage);
            }
        }

        public static void Reset()
        {
            messageCounter = 0;
        }
    }
}
