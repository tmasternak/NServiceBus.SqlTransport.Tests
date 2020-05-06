using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using NServiceBus.SqlTransport.Tests.Shared;

namespace NServiceBus.SqlTransport.Tests.Monitor
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var endpointName = args.Length > 0 ? args[0] : Shared.Configuration.ReceiverEndpointName;

            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.InstrumentationKey = Configuration.AppInsightKey;

            var telemetryClient = new TelemetryClient(configuration);
            telemetryClient.TrackTrace("Monitor started");

            Console.WriteLine("Cleaning wait_time stats ...");

            await ClearWaitTimeStats();

            Console.WriteLine("Monitor started");

            while (true)
            {
                var queueLengthMetric = await GetQueueLengthMetric(endpointName);

                telemetryClient.TrackMetric(queueLengthMetric);

                var pageLatchMetrics = await GetPageLatchStats();

                pageLatchMetrics.ToList().ForEach(m => telemetryClient.TrackMetric(m));

                telemetryClient.Flush();

                Console.WriteLine($"[{DateTime.Now.ToLocalTime()}] Metrics pushed to AppInsights");

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        static async Task ClearWaitTimeStats()
        {
            using (var connection = new SqlConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand("DBCC SQLPERF ('sys.dm_os_wait_stats', CLEAR); ", connection))
                {
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        static async Task<MetricTelemetry> GetQueueLengthMetric(string endpointName)
        {
            var queueLength = await QueueLengthMonitor.GetQueueLengthMetric(endpointName);
           
            return new MetricTelemetry
            {
                Name = $"queue length - {endpointName}",
                Sum = (int) queueLength,
                Count = 1
            };
        }

        static async Task<MetricTelemetry[]> GetPageLatchStats()
        {
            var query =
                @"select waiting_tasks_count, wait_time_s, max_wait_time_ms, signal_wait_time_s from qpi.wait_stats where wait_type = 'PAGELATCH_EX'";

            using (var connection = new SqlConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand(query, connection))
                {
                    var result = await command.ExecuteReaderAsync();
                    if (result.HasRows)
                    {
                        result.Read();

                        return new[]
                        {
                            new MetricTelemetry
                            {
                                Name = "PAGELATCH_EX - waiting_tasks_count",
                                Sum = (long?) result[0] ?? 0,
                                Count = 1
                            },
                            new MetricTelemetry
                            {
                                Name = "PAGELATCH_EX - wait_time_s",
                                Sum = decimal.ToDouble((decimal) (result[1] ?? 0)),
                                Count = 1
                            },
                            new MetricTelemetry
                            {
                                Name = "PAGELATCH_EX - max_wait_time_ms",
                                Sum = (long?) result[2] ?? 0,
                                Count = 1
                            },
                            new MetricTelemetry
                            {
                                Name = "PAGELATCH_EX - signal_wait_time_s",
                                Sum = (long?) result[3] ?? 0,
                                Count = 1
                            }
                        };
                    }

                    return new MetricTelemetry[0];
                }
            }
        }
    }
}