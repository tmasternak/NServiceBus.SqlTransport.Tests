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

            TelemetryClient telemetryClient = null;

            if (Configuration.AppInsightKey != null)
            {
                var configuration = TelemetryConfiguration.CreateDefault();
                configuration.InstrumentationKey = Configuration.AppInsightKey;
                telemetryClient = new TelemetryClient(configuration);
            }

            if (args.Contains("--reset"))
            {
                Console.WriteLine("Cleaning wait_time stats ...");
                await ClearWaitTimeStats();
            }

            telemetryClient?.TrackTrace("Monitor started");

            Console.WriteLine("Monitor started");

            DateTime RoundUp(DateTime dt, TimeSpan d)
            {
                return new DateTime((dt.Ticks + d.Ticks - 1) / d.Ticks * d.Ticks, dt.Kind);
            }

            var interval = TimeSpan.FromSeconds(30); // Report granulatity is 1 minute, must be reporting more often to ensure every minute has atleast one sample.
            var now = DateTime.UtcNow;
            var next = RoundUp(now, interval);
            var delay = next - now;
            await Task.Delay(delay); // Align clock

            while (true)
            {
                try
                {
                    async Task Invoke()
                    {
                        var pageLatchMetrics = await GetPageLatchStats();

                        if (telemetryClient != null)
                        {
                            pageLatchMetrics.ToList().ForEach(m => telemetryClient.TrackMetric(m));
                            await foreach (var item in GetQueueLengthsMetric()) telemetryClient.TrackMetric(item);
                            telemetryClient.Flush();
                        }
                    }
                    now = DateTime.UtcNow; // Keeps invocation spot on the interval
                    next += interval;
                    delay = next - now;
                    await Task.WhenAll(
                        Task.Delay(delay),
                        Invoke()
                        );
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
                    Count = 1                                                   // Maybe  this could just the time period like the ticks?
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - wait_time_s (delta)",
                    Sum = delta.wait_time_s,
                    Count = 1,
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - signal_wait_time_s (delta)",
                    Sum = delta.signal_wait_time_s,
                    Count = 1
                },

                // From DELTA RAW
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - waiting_tasks_count_raw (delta)",
                    Sum = delta.waiting_tasks_count_raw,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - wait_time_s_raw (delta)",
                    Sum = delta.wait_time_s_raw,
                    Count = 1
                },
                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - signal_wait_time_s_raw (delta)",
                    Sum = delta.signal_wait_time_s_raw,
                    Count = 1
                },

                new MetricTelemetry
                {
                    Name = "PAGELATCH_EX - delta_s (delta)",
                    Sum = delta.ticks/(double)Stopwatch.Frequency,
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
