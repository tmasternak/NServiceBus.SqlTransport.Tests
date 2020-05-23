using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
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
            Console.WriteLine(Environment.CommandLine);
            var id = DeterministicGuid.Create(args[0]);
            var mutexName = id.ToString("N");

            using var appSingleton = new System.Threading.Mutex(false, mutexName, out var createdNew);

            if (!createdNew)
            {
                Console.WriteLine("Single instance!");
                return;
            }

            Configuration.ConnectionString = args[0];

            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.InstrumentationKey = Configuration.AppInsightKey;

            var telemetryClient = new TelemetryClient(configuration);
            telemetryClient.TrackTrace("Monitor started");

            Console.WriteLine("Cleaning wait_time stats ...");

            await ClearWaitTimeStats();

            Console.WriteLine("Monitor started");

            while (true)
            {
                try
                {
                    await foreach (var item in GetQueueLengthsMetric())
                    {
                        telemetryClient.TrackMetric(item);
                    }

                    var pageLatchMetrics = await GetPageLatchStats();

                    pageLatchMetrics.ToList().ForEach(m => telemetryClient.TrackMetric(m));

                    telemetryClient.Flush();

                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.BackgroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine(ex);
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.BackgroundColor = ConsoleColor.Black;
                }
            }
        }

        static async Task ClearWaitTimeStats()
        {
            using var connection = new SqlConnection(Configuration.ConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand("DBCC SQLPERF ('sys.dm_os_wait_stats', CLEAR); ", connection);
            await command.ExecuteNonQueryAsync();
        }

        static async IAsyncEnumerable<MetricTelemetry> GetQueueLengthsMetric()
        {
            using var connection = new SqlConnection(Configuration.ConnectionString);

            var query = @"

                SELECT p.rows,t.name
                FROM sys.partitions AS p
	                INNER JOIN sys.tables AS t   ON p.[object_id] = t.[object_id]
	                INNER JOIN sys.schemas AS s  ON s.[schema_id] = t.[schema_id]
                WHERE
	                p.rows>0
	                AND p.index_id IN (0,1);";

            foreach (var table in await connection.QueryAsync(query))
            {
                yield return new MetricTelemetry
                {
                    Name = $"Queue Length",
                    Sum = (int)table.rows,
                    Count = 1,
                    Properties = { { "Table", table.name } }

                };
            }
        }

        static wait_stats previous;

        static async Task<MetricTelemetry[]> GetPageLatchStats()
        {
            var query = @"select waiting_tasks_count, wait_time_s, max_wait_time_ms, signal_wait_time_s, wait_per_task_ms from qpi.wait_stats where wait_type = 'PAGELATCH_EX'";

            using var connection = new SqlConnection(Configuration.ConnectionString);

            var result = await connection.QueryFirstOrDefaultAsync<wait_stats>(query);
            result.ticks = Stopwatch.GetTimestamp();

            var delta = result.Subtract(previous);

            Console.WriteLine(delta);

            var metrics = new[]
            {
                // From DELTA
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - waiting_tasks_count (delta)",
                    Sum = delta.waiting_tasks_count,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - wait_time_s (delta)",
                    Sum = delta.wait_time_s,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - signal_wait_time_s (delta)",
                    Sum = delta.signal_wait_time_s,
                    Count = 1
                },
                
                // From RESULT
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - waiting_tasks_count",
                    Sum = result.waiting_tasks_count,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - wait_time_s",
                    Sum = result.wait_time_s,
                    Count = 1
                },
                new MetricTelemetry()
                {
                    Name = "PAGELATCH_EX - max_wait_time_ms",
                    Sum = result.max_wait_time_ms,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - signal_wait_time_s",
                    Sum = result.signal_wait_time_s,
                    Count = 1
                },
                new MetricTelemetry()
                {

                    Name = "PAGELATCH_EX - signal_wait_time_s",
                    Sum = result.signal_wait_time_s,
                    Count = 1
                } ,
                new MetricTelemetry()
                {

                    Name = "PAGELATCH_EX - wait_per_task_ms",
                    Sum = result.wait_per_task_ms,
                    Count = 1
                }
            };

            previous = result;
            return metrics;

        }
    }
}
