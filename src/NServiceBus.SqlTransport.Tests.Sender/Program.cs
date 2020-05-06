using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NServiceBus.SqlTransport.Tests.Shared;

namespace NServiceBus.SqlTransport.Tests.Sender
{
    class Program
    {
        static async Task Main(string[] a)
        {
            var configuration = new EndpointConfiguration(Shared.Configuration.SenderEndpointName);

            configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing().RouteToEndpoint(typeof(TestCommand).Assembly, Shared.Configuration.ReceiverEndpointName);

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.Conventions().DefiningCommandsAs(t => t == typeof(TestCommand));

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            var commands = new (string, Func<CancellationToken, string[], Task>)[]
            {
                ("f|Fill the sender queue. Syntax: f <number of messages> <number of tasks> <destination>",
                    async (ct, args) =>
                    {
                        var totalMessages = args.Length > 0 ? int.Parse(args[0]) : 1000;
                        var numberOfTasks = args.Length > 1 ? int.Parse(args[1]) : 5;

                        var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
                        {
                            for (var i = 0; i < totalMessages / numberOfTasks; i++)
                            {
                                var op = new SendOptions();
                                if (args.Length > 2)
                                {
                                    op.SetDestination(args[2]);
                                }

                                await endpoint.Send(new TestCommand(), op);
                            }
                        }).ToArray();

                        await Task.WhenAll(tasks);
                    }),
                ("s|Start sending messages to the queue. Syntax: s <number of tasks> <destination>", async (ct, args) =>
                {
                    var numberOfTasks = args.Length > 0 ? int.Parse(args[0]) : 5;

                    var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
                    {
                        while (ct.IsCancellationRequested == false)
                        {
                            var op = new SendOptions();
                            if (args.Length > 2)
                            {
                                op.SetDestination(args[2]);
                            }

                            await endpoint.Send(new TestCommand(), op);
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks);
                }),
                ("t|Throttled sending that keeps the receiver queue size at n. Syntax: t <number of msgs> <destination>",
                    async (ct, args) =>
                    {
                        var maxSenderCount = 20;
                        var taskBarriers = new int[maxSenderCount];

                        var numberOfMessages = int.Parse(args[0]);
                        var destination = args[1];

                        var semaphore = new SemaphoreSlim(0, maxSenderCount);

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
                    }),
                ("r|Reset receiver statistics", async (ct, args) => { await endpoint.Send(new ResetCommand()); })
            };

            await Run(commands);
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