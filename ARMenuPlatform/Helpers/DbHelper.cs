using Microsoft.Data.SqlClient;
using System.Data;

namespace ARMenuPlatform.Helpers
{
    /// <summary>
    /// Centralised ADO.NET helper for all SQL Server operations.
    /// Register as a scoped service in Program.cs and inject via constructor DI.
    /// Uses Microsoft.Data.SqlClient (NOT the legacy System.Data.SqlClient).
    /// </summary>
    public class DbHelper
    {
        private readonly string _connectionString;

        // IConfiguration is injected by ASP.NET Core's DI container.
        // The connection string name "ARMenuDB" must match appsettings.json exactly.
        public DbHelper(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("ARMenuDB")
                ?? throw new InvalidOperationException(
                    "Connection string 'ARMenuDB' not found in appsettings.json. " +
                    "Ensure the ConnectionStrings section is configured correctly.");
        }

        // -------------------------------------------------------------------------
        // ExecuteQuery
        // Use for: SELECT statements that return rows.
        // Returns:  DataTable — empty table (no rows) if query returns nothing.
        //
        // Usage example:
        //   var dt = _db.ExecuteQuery(
        //       "SELECT * FROM Restaurants WHERE Status = @Status",
        //       new SqlParameter("@Status", "Active"));
        // -------------------------------------------------------------------------
        public DataTable ExecuteQuery(string sql, params SqlParameter[] parameters)
        {
            var dataTable = new DataTable();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30; // seconds — raise for long-running reports

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            connection.Open();

            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }

        // -------------------------------------------------------------------------
        // ExecuteQueryAsync
        // Async version of ExecuteQuery for use in async controller actions.
        //
        // Usage example:
        //   var dt = await _db.ExecuteQueryAsync(
        //       "SELECT * FROM Dishes WHERE RestaurantID = @Id",
        //       new SqlParameter("@Id", restaurantId));
        // -------------------------------------------------------------------------
        public async Task<DataTable> ExecuteQueryAsync(string sql, params SqlParameter[] parameters)
        {
            var dataTable = new DataTable();

            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30;

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            await connection.OpenAsync();

            // SqlDataAdapter doesn't support async natively.
            // Use SqlDataReader for true async fill:
            await using var reader = await command.ExecuteReaderAsync();
            dataTable.Load(reader);

            return dataTable;
        }

        // -------------------------------------------------------------------------
        // ExecuteNonQuery
        // Use for: INSERT, UPDATE, DELETE, stored procedures with no result set.
        // Returns:  int — number of rows affected (0 means nothing changed).
        //
        // Usage example:
        //   int rows = _db.ExecuteNonQuery(
        //       "UPDATE Restaurants SET Status = @Status WHERE RestaurantID = @Id",
        //       new SqlParameter("@Status", "Paused"),
        //       new SqlParameter("@Id",     restaurantId));
        // -------------------------------------------------------------------------
        public int ExecuteNonQuery(string sql, params SqlParameter[] parameters)
        {
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30;

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            connection.Open();
            return command.ExecuteNonQuery();
        }

        // -------------------------------------------------------------------------
        // ExecuteNonQueryAsync
        // Async version of ExecuteNonQuery.
        //
        // Usage example:
        //   int rows = await _db.ExecuteNonQueryAsync(
        //       "INSERT INTO Scans (RestaurantID, TableID, ScanType, DeviceType) " +
        //       "VALUES (@RID, @TID, @SType, @DType)",
        //       new SqlParameter("@RID",   restaurantId),
        //       new SqlParameter("@TID",   tableId),
        //       new SqlParameter("@SType", "MenuOpen"),
        //       new SqlParameter("@DType", "Android"));
        // -------------------------------------------------------------------------
        public async Task<int> ExecuteNonQueryAsync(string sql, params SqlParameter[] parameters)
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30;

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            await connection.OpenAsync();
            return await command.ExecuteNonQueryAsync();
        }

        // -------------------------------------------------------------------------
        // ExecuteScalar
        // Use for: queries that return a single value — COUNT, MAX, SCOPE_IDENTITY(),
        //          or any SELECT returning one column, one row.
        // Returns:  object? — cast to the type you expect. Returns null if no rows.
        //
        // Usage example:
        //   int count = Convert.ToInt32(
        //       _db.ExecuteScalar("SELECT COUNT(*) FROM Dishes WHERE RestaurantID = @Id",
        //           new SqlParameter("@Id", restaurantId)));
        // -------------------------------------------------------------------------
        public object? ExecuteScalar(string sql, params SqlParameter[] parameters)
        {
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30;

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            connection.Open();

            var result = command.ExecuteScalar();

            // ExecuteScalar returns DBNull when the query returns NULL from SQL.
            // Normalise DBNull to C# null so callers don't have to check both.
            return result == DBNull.Value ? null : result;
        }

        // -------------------------------------------------------------------------
        // ExecuteScalarAsync
        // Async version of ExecuteScalar.
        //
        // Usage example:
        //   string? email = (string?) await _db.ExecuteScalarAsync(
        //       "SELECT OwnerEmail FROM Restaurants WHERE RestaurantID = @Id",
        //       new SqlParameter("@Id", restaurantId));
        // -------------------------------------------------------------------------
        public async Task<object?> ExecuteScalarAsync(string sql, params SqlParameter[] parameters)
        {
            await using var connection = new SqlConnection(_connectionString);
            await using var command = new SqlCommand(sql, connection);

            command.CommandType = CommandType.Text;
            command.CommandTimeout = 30;

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            await connection.OpenAsync();

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value ? null : result;
        }

        // -------------------------------------------------------------------------
        // ExecuteStoredProcedure
        // Use for: calling named stored procedures (if you add them later).
        // Returns:  DataTable of results; empty table if SP returns no rows.
        //
        // Usage example:
        //   var dt = _db.ExecuteStoredProcedure(
        //       "sp_GetRestaurantMenu",
        //       new SqlParameter("@RestaurantID", restaurantId));
        // -------------------------------------------------------------------------
        public DataTable ExecuteStoredProcedure(string procedureName, params SqlParameter[] parameters)
        {
            var dataTable = new DataTable();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(procedureName, connection);

            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 60; // SPs may do more work — give extra time

            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);

            connection.Open();

            using var adapter = new SqlDataAdapter(command);
            adapter.Fill(dataTable);

            return dataTable;
        }
    }
}