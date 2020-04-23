using System;
using System.Linq;
using System.Threading.Tasks;
using NServiceBus.SqlTransport.Tests.Shared;

namespace NServiceBus.SqlTransport.Tests.Sender
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var configuration = new EndpointConfiguration(Shared.Configuration.SenderEndpointName);

            configuration.UseTransport<SqlServerTransport>()
                .ConnectionString(() => Shared.Configuration.ConnectionString)
                .Routing().RouteToEndpoint(typeof(TestCommand), Shared.Configuration.ReceiverEndpointName);

            configuration.UsePersistence<InMemoryPersistence>();

            configuration.Conventions().DefiningCommandsAs(t => t == typeof(TestCommand));

            configuration.EnableInstallers();

            var endpoint = await Endpoint.Start(configuration);

            var commands = new (string, Func<Task>)[]
            {
                ("f|Fill the sender queue with 1000 msgs", async () =>
                {
                    var totalMessages = 1000;
                    var numberOfTasks = 5;

                    var tasks = Enumerable.Range(1, numberOfTasks).Select(async _ =>
                    {
                        for (var i = 0; i < totalMessages / numberOfTasks; i++)
                        {
                            await endpoint.Send(new TestCommand());
                        }
                    }).ToArray();

                    await Task.WhenAll(tasks);
                })
            };

            await Run(commands);

        }

        static async Task Run((string, Func<Task>)[] commands)
        {
            Console.WriteLine("Select command:");
            commands.Select(i => i.Item1).ToList().ForEach(Console.WriteLine);

            while (true)
            {
                Console.Write("Press command <key>: ");

                var key = Console.ReadKey();

                var match = commands.Where(c => c.Item1.StartsWith(key.KeyChar)).ToArray();

                if (match.Any())
                {
                    var command = match.First();

                    Console.WriteLine($"\nExecuting: {command.Item1.Split('|')[1]}");

                    await command.Item2();

                    Console.WriteLine("Done");
                }
            }
        }
    }
}
