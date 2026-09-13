using Microsoft.Data.SqlClient;

namespace DocumentProcessing.Infrastructure.Resilience;

internal static class SqlTransient
{
    // Numbers pulled from Microsoft's SQL transient-fault guidance (deadlock, transport drop, Azure throttling).
    private static readonly HashSet<int> TransientNumbers =
    [
        -2, 20, 64, 233, 1205, 10053, 10054, 10060, 10928, 10929,
        40143, 40197, 40501, 40540, 40613, 40615, 49918, 49919, 49920
    ];

    public static bool Matches(SqlException ex)
    {
        if (TransientNumbers.Contains(ex.Number))
            return true;

        foreach (SqlError err in ex.Errors)
        {
            if (TransientNumbers.Contains(err.Number))
                return true;
        }

        return false;
    }
}
