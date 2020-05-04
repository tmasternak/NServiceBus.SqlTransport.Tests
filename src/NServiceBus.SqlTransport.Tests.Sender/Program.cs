using System;
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
                ("f|Fill the sender queue. Syntax: f <number of messages> <number of tasks>", async (ct, args) =>
                {
                    var totalMessages = args.Length > 0 ? int.Parse(args[0]) : 1000;
                    var numberOfTasks = args.Length > 1 ? int.Parse(args[1]) : 5;

                    var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
                    {
                        for (var i = 0; i < totalMessages / numberOfTasks; i++)
                        {
                            await endpoint.Send(new TestCommand());
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks);
                }),
                ("s|Start sending messages to the queue. Syntax: s <number of tasks>", async (ct, args) =>
                {
                    var numberOfTasks = args.Length > 0 ? int.Parse(args[0]) : 5;

                    var ctSource = new CancellationTokenSource();

                    var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
                    {
                        while (ct.IsCancellationRequested == false)
                        {
                            await endpoint.Send(new TestCommand());
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks);
                }),
                ("r|Reset receiver statistics", async (ct, args) =>
                {
                    await endpoint.Send(new ResetCommand());
                })
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
