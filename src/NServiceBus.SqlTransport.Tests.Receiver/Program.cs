using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Configuration.AdvancedExtensibility;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NServiceBus.SqlTransport.Tests.Shared;
using Console = System.Console;

namespace NServiceBus.SqlTransport.Tests.Receiver
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var arguments = CommandLineOptions.Parse(args);

            var endpointName = arguments.TryGetSingle("e") ?? Shared.Configuration.ReceiverEndpointName;

            TestHandler.MaxDelay = arguments.TryGetMany<int>("d", 2, 2) ?? new[] { 0, 0 };
            TestHandler.OutgoingMessages = arguments.TryGetSingle<int?>("o") ?? 0;

            var processingTimeHistoBucketParam = arguments.TryGetSingle<int?>("pth") ?? 10;
            var receiveIntervalHistoBucketParam = arguments.TryGetSingle<int?>("rih") ?? 10;

            var dumpInterval = arguments.TryGetSingle<int?>("di") ?? 100000;

            var processingTimeHisto = new Histogram(20, (int) (Stopwatch.Frequency / processingTimeHistoBucketParam));
            var receiveIntervalHisto = new Histogram(20, (int)(Stopwatch.Frequency / receiveIntervalHistoBucketParam));

            var configuration = new EndpointConfiguration(endpointName);

            configuration.GetSettings().Set("SqlServer.DisableDelayedDelivery", true);

            var routing = configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing();

            routing.RouteToEndpoint(typeof(FollowUpMessage), Shared.Configuration.ReceiverEndpointName);

            configuration.Conventions().DefiningMessagesAs(t => t == typeof(FollowUpMessage));
            configuration.Pipeline.Register(new ReceiveBehavior(receiveIntervalHisto), "Processing interval histogram");

            configuration.Pipeline.OnReceivePipelineCompleted(completed =>
            {
                Statistics.MessageProcessed();
                var processingTimeTicks =
                    (completed.CompletedAt - completed.StartedAt).TotalSeconds * Stopwatch.Frequency;

                processingTimeHisto.NewValue((long)processingTimeTicks);
                return Task.CompletedTask;
            });

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            var tokenSource = new CancellationTokenSource();

            var dumpStatsTask = Task.Run(async () =>
            {
                while (!tokenSource.IsCancellationRequested)
                {
                    await Task.Delay(dumpInterval, tokenSource.Token);
                    receiveIntervalHisto.Dump("Receive Interval");
                    processingTimeHisto.Dump("Processing Time");
                }

            }, tokenSource.Token);

            Console.WriteLine("Press <enter> to exit.");
            Console.ReadLine();

            tokenSource.Cancel();

            await dumpStatsTask;
            await endpoint.Stop();
        }
    }

    class ReceiveBehavior : Behavior<ITransportReceiveContext>
    {
        Histogram histogram;
        long lastTimestamp;

        public ReceiveBehavior(Histogram histogram)
        {
            this.histogram = histogram;
        }

        public override Task Invoke(ITransportReceiveContext context, Func<Task> next)
        {
            long previousTimestamp;
            long nextTimestamp;
            do
            {
                nextTimestamp = Stopwatch.GetTimestamp();
                previousTimestamp = lastTimestamp;

            } while (Interlocked.CompareExchange(ref lastTimestamp, nextTimestamp, previousTimestamp) != previousTimestamp);

            histogram.NewValue(nextTimestamp - previousTimestamp);

            return next();
        }
    }

    class TestHandler : IHandleMessages<TestCommand>
    {
        public static int[] MaxDelay;
        public static int OutgoingMessages;

        static Random random = new Random();

        public async Task Handle(TestCommand message, IMessageHandlerContext context)
        {
            await Task.Delay(random.Next(MaxDelay[0], MaxDelay[1]));

            for (var i = 0; i < OutgoingMessages; i++)
            {
                await context.Send(new FollowUpMessage()).ConfigureAwait(false);
            }
        }
    }

    class Histogram
    {
        int numberOfBuckets;
        long ticksPerBucket;
        int[] buckets;

        public Histogram(int numberOfBuckets, int ticksPerBucket)
        {
            this.numberOfBuckets = numberOfBuckets;
            this.ticksPerBucket = ticksPerBucket;
            buckets = new int[numberOfBuckets + 1]; //last is overflow bucket
        }

        public void NewValue(long value)
        {
            var bucket = value / ticksPerBucket;

            if (bucket > numberOfBuckets)
            {
                Interlocked.Increment(ref buckets[numberOfBuckets]);
            }
            else
            {
                Interlocked.Increment(ref buckets[bucket]);
            }
        }

        public void Dump(string loggerName)
        {
            var log = LogManager.GetLogger(loggerName);

            var logMessage = string.Join(Environment.NewLine, buckets.Select((v, i) =>
            {
                var threshold = (i * ticksPerBucket * 1000) / Stopwatch.Frequency; //in milliseconds
                return $"{threshold}: {v}";
            }));

            Console.WriteLine(loggerName);
            Console.WriteLine(logMessage);
            log.Info(logMessage);
        }
    }

    class Statistics
    {
        const int Interval = 500;
        static int messageCounter;
        static long previousTimestamp;
        static ILog log;

        public static void MessageProcessed()
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
