using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.DragonFruit;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.SqlTransport.Tests.Shared;

namespace NServiceBus.SqlTransport.Tests.Sender
{
    public class Program
    {
        static CancellationToken ct;
        static IEndpointInstance endpoint;

        static async Task Main(string[] args)
        {
            var configuration = new EndpointConfiguration(Shared.Configuration.SenderEndpointName);

            configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing().RouteToEndpoint(typeof(TestCommand).Assembly, Shared.Configuration.ReceiverEndpointName);

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.Conventions().DefiningCommandsAs(t => t == typeof(TestCommand));

            configuration.EnableInstallers();

            endpoint = await Endpoint.Start(configuration);

            var rootCommand = new RootCommand();

            rootCommand.AddCommand(CreateChildCommand(nameof(Send), "send"));
            rootCommand.AddCommand(CreateChildCommand(nameof(ThrottledSend), "throttled-send"));
            rootCommand.AddCommand(CreateChildCommand(nameof(FillQueue), "fill"));
            rootCommand.AddCommand(CreateChildCommand(nameof(ResetStatistics), "reset"));

            var ctSource = new CancellationTokenSource();
            ct = ctSource.Token;

            var task = rootCommand.InvokeAsync(args);

            while (ctSource.IsCancellationRequested == false && task.IsCompleted == false)
            {
                if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Enter)
                {
                    ctSource.Cancel();
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            await task;
        }

        static Command CreateChildCommand(string methodName, string name)
        {
            var methodInfo = typeof(Program).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            
            var childCommand = new Command(name);
            childCommand.ConfigureFromMethod(methodInfo);
            return childCommand;
        }

        static async Task ResetStatistics()
        {
            await endpoint.Send(new ResetCommand());
        }

        static async Task ThrottledSend(int numberOfMessages, string destination, int maxSenderCount = 20)
        {
            var taskBarriers = new int[maxSenderCount];

            var monitor = Task.Run(async () =>
            {
                var nextTask = 0;

                while (ct.IsCancellationRequested == false)
                {
                    try
                    {
                        var queueLength = await QueueLengthMonitor.GetQueueLengthMetric(destination);
                        var delta = numberOfMessages - queueLength;

                        if (delta > 0)
                        {
                            Interlocked.Exchange(ref taskBarriers[nextTask], 1);

                            nextTask = Math.Min(maxSenderCount - 1, nextTask + 1);
                        }
                        else
                        {
                            nextTask = Math.Max(0, nextTask - 1);

                            Interlocked.Exchange(ref taskBarriers[nextTask], 0);
                        }

                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                    catch
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }
            }, ct);


            var senders = Enumerable.Range(0, maxSenderCount).Select(async taskNo =>
            {
                while (ct.IsCancellationRequested == false)
                {
                    try
                    {
                        var allowed = Interlocked.CompareExchange(ref taskBarriers[taskNo], 1, 1);

                        if (allowed == 1)
                        {
                            var op = new SendOptions();
                            op.SetDestination(destination);

                            await endpoint.Send(new TestCommand(), op);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        }
                    }
                    catch
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }
                    }
                }
            }).ToArray();

            await Task.WhenAll(new List<Task>(senders) {monitor});
        }

        static async Task Send(int numberOfTasks = 5, int sendDelayMs = 0, string destination = null)
        {
            var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
            {
                while (ct.IsCancellationRequested == false)
                {
                    try
                    {
                        var op = new SendOptions();
                        if (destination != null)
                        {
                            op.SetDestination(destination);
                        }

                        await endpoint.Send(new TestCommand(), op);

                        await Task.Delay(TimeSpan.FromMilliseconds(sendDelayMs), ct);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

        static async Task FillQueue(int totalMessages = 1000, int numberOfTasks = 5, string destination = null)
        {
            var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
            {
                for (var i = 0; i < totalMessages / numberOfTasks; i++)
                {
                    var op = new SendOptions();
                    if (destination != null)
                    {
                        op.SetDestination(destination);
                    }

                    await endpoint.Send(new TestCommand(), op);
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }


        static async Task Run((string, Func<CancellationToken, string[], Task>)[] commands)
        {
            Console.WriteLine("Select command:");
            commands.Select(i => i.Item1).ToList().ForEach(Console.WriteLine);

            while (true)
            {
                var commandLine = Console.ReadLine();
                if (commandLine == null)
                {
                    continue;
                }

                var parts = commandLine.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
                var key = parts.First().ToLowerInvariant();
                var arguments = parts.Skip(1).ToArray();

                var match = commands.Where(c => c.Item1.StartsWith(key)).ToArray();

                if (match.Any())
                {
                    var command = match.First();

                    Console.WriteLine($"\nExecuting: {command.Item1.Split('|')[1]}");

                    using (var ctSource = new CancellationTokenSource())
                    {
                        var task = command.Item2(ctSource.Token, arguments);

                        while (ctSource.IsCancellationRequested == false && task.IsCompleted == false)
                        {
                            if (Console.KeyAvailable && Console.ReadKey().Key == ConsoleKey.Enter)
                            {
                                ctSource.Cancel();
                                break;
                            }

                            await Task.Delay(TimeSpan.FromMilliseconds(500));
                        }

                        await task;
                    }

                    Console.WriteLine("Done");
                }
            }
        }
    }
}