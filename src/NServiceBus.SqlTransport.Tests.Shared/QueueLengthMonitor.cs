using System.Data.SqlClient;
using System.Threading.Tasks;

namespace NServiceBus.SqlTransport.Tests.Shared
{
    public class QueueLengthMonitor
    {
        public static async Task<int> GetQueueLengthMetric(string endpointName)
        {
            var query =
                $@"SELECT isnull(cast(max([RowVersion]) - min([RowVersion]) + 1 AS int), 0) FROM [{endpointName}] WITH (nolock)";

            using (var connection = new SqlConnection(Configuration.ConnectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand(query, connection))
                {
                    var result = await command.ExecuteScalarAsync();

                    return (int) result;
                }
            }
        }
    }
}