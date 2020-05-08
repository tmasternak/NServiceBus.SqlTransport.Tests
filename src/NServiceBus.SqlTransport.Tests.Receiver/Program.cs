using System;
using System.CommandLine;
using System.CommandLine.DragonFruit;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NServiceBus.SqlTransport.Tests.Shared;
using Console = System.Console;

namespace NServiceBus.SqlTransport.Tests.Receiver
{
    class Program
    {
        static Task Main(string[] args)
        {
            var rootCommand = new RootCommand("run a receiver endpoint");
                
            rootCommand.ConfigureFromMethod(typeof(Program).GetMethod(nameof(RunEndpoint), BindingFlags.Static | BindingFlags.NonPublic));

            return rootCommand.InvokeAsync(args);
        }

        static async Task RunEndpoint(
            string endpointName = Shared.Configuration.ReceiverEndpointName,
            int maxDelay = 0,
            int minDelay = 0,
            int outgoingMessages = 0,
            int pth = 10,
            int rih = 10,
            int dumpInterval = 10000)
        {
            TestHandler.MaxDelay = new[] {minDelay, maxDelay};
            TestHandler.OutgoingMessages = outgoingMessages;

            var processingTimeHistogram = new Histogram(20, (int) (Stopwatch.Frequency / pth));
            var receiveIntervalHistogram = new Histogram(20, (int)(Stopwatch.Frequency / rih));

            var configuration = new EndpointConfiguration(endpointName);

            var routing = configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing();

            routing.RouteToEndpoint(typeof(FollowUpMessage), Shared.Configuration.ReceiverEndpointName);

            configuration.Conventions().DefiningMessagesAs(t => t == typeof(FollowUpMessage));
            configuration.Pipeline.Register(new ReceiveBehavior(receiveIntervalHistogram), "Processing interval histogram");

            configuration.Pipeline.OnReceivePipelineCompleted(completed =>
            {
                Statistics.MessageProcessed();
                var processingTimeTicks =
                    (completed.CompletedAt - completed.StartedAt).TotalSeconds * Stopwatch.Frequency;

                processingTimeHistogram.NewValue((long)processingTimeTicks);
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
                    try
                    {
                        await Task.Delay(dumpInterval, tokenSource.Token);
                        receiveIntervalHistogram.Dump("Receive Interval");
                        processingTimeHistogram.Dump("Processing Time");
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
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
