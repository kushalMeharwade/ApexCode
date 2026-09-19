using System;
using System.Data;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ApexCode.Helpers;

public static class DatabaseConnectionHelper
{
    /// <summary>
    /// Opens a SQL connection with a bullet-proof retry mechanism and certificate fallback.
    /// </summary>
    public static async Task<SqlConnection> OpenConnectionAsync(string connectionString, ILogger logger = null, CancellationToken cancellationToken = default)
    {
        SqlConnection sqlConnection = null;
        int maxRetries = 3;
        int currentTry = 0;

        while (currentTry <= maxRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sqlConnection = new SqlConnection(connectionString);

                // Defensive check to ensure connection isn't already open
                if (sqlConnection.State == ConnectionState.Open)
                {
                    sqlConnection.Close();
                }

                await sqlConnection.OpenAsync(cancellationToken);
                return sqlConnection; // Connection successful
            }
            catch (OperationCanceledException)
            {
                sqlConnection?.Dispose();
                throw;
            }
            catch (SqlException ex)
            {
                if (sqlConnection != null)
                {
                    sqlConnection.Dispose();
                    sqlConnection = null;
                }

                // If the error is about untrusted connection, add TrustServerCertificate in the connection string
                if (ex.Message.Contains("certificate chain") || ex.Message.Contains("not trusted") || ex.Message.Contains("TrustServerCertificate"))
                {
                    var builder = new SqlConnectionStringBuilder(connectionString);
                    if (!builder.TrustServerCertificate)
                    {
                        builder.TrustServerCertificate = true;
                        connectionString = builder.ConnectionString;
                        logger?.LogWarning("Connection failed due to untrusted certificate. Added TrustServerCertificate=True and retrying immediately.");
                    }
                }

                if (currentTry >= maxRetries)
                {
                    logger?.LogError(ex, "Failed to connect to the database after {MaxRetries} retries.", maxRetries);
                    throw new Exception("The server is not reachable. Ensure the database server is running and accessible.", ex);
                }

                currentTry++;
                await Task.Delay(1000 * currentTry, cancellationToken);
            }
            catch (Exception ex)
            {
                if (sqlConnection != null)
                {
                    sqlConnection.Dispose();
                    sqlConnection = null;
                }

                if (currentTry >= maxRetries)
                {
                    logger?.LogError(ex, "Failed to connect to the database after {MaxRetries} retries.", maxRetries);
                    throw new Exception("The server is not reachable. Ensure the database server is running and accessible.", ex);
                }

                currentTry++;
                await Task.Delay(1000 * currentTry, cancellationToken);
            }
        }

        throw new Exception("The server is not reachable. Ensure the database server is running and accessible.");
    }
}
