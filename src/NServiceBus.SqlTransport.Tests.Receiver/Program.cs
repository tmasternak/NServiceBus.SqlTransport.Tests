using System;
using System.CommandLine;
using System.CommandLine.DragonFruit;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using NServiceBus.Configuration.AdvancedExtensibility;
using NServiceBus.Logging;
using NServiceBus.Pipeline;
using NServiceBus.SqlTransport.Tests.Shared;
using Console = System.Console;

namespace NServiceBus.SqlTransport.Tests.Receiver
{
    public class Program
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

            configuration.GetSettings().Set("SqlServer.DisableDelayedDelivery", true);

            var routing = configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing();

            routing.RouteToEndpoint(typeof(FollowUpMessage), Shared.Configuration.ReceiverEndpointName);

            configuration.Conventions().DefiningMessagesAs(t => t == typeof(FollowUpMessage));
            configuration.Pipeline.Register(new ReceiveBehavior(receiveIntervalHistogram), "Processing interval histogram");

            var statistics = new Statistics();
            statistics.Initialize(endpointName);

            configuration.Pipeline.OnReceivePipelineCompleted(completed =>
            {
                statistics.MessageProcessed();
                var processingTimeTicks =
                    (completed.CompletedAt - completed.StartedAt).TotalSeconds * Stopwatch.Frequency;

                processingTimeHistogram.NewValue((long)processingTimeTicks);
                return Task.CompletedTask;
            });

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            var tokenSource = new CancellationTokenSource();

            var dumpStatsTask = Task.CompletedTask;

            if (dumpInterval > 0)
            {
                dumpStatsTask = Task.Run(async () =>
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
            }


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
        int messageCounter;
        long previousTimestamp;
        TelemetryClient telemetryClient;

        static ILog log;
        string receiverName;

        public void Initialize(string receiverName)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.InstrumentationKey = Shared.Configuration.AppInsightKey;

            telemetryClient = new TelemetryClient(configuration);

            this.receiverName = receiverName;
        }

        public void MessageProcessed()
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

                var throughputValue = new MetricTelemetry
                {
                    Name = $"{receiverName} - Throughput [msg/sec]",
                    Sum = throughput,
                    Count = 1
                };

                telemetryClient.TrackMetric(throughputValue);

                var logMessage = $"{DateTime.Now:s};{throughput};{newValue}";
                Console.WriteLine(logMessage);
                log.Info(logMessage);
            }
        }
    }
}
