using System.Data;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Shared command execution helpers for repositories that already own SQL composition and result mapping.
    /// </summary>
    internal static class DbCommandExecution
    {
        public static DbCommand CreateCommand(
            DbConnection connection,
            string sql,
            IReadOnlyList<DbParameter>? parameters = null,
            DbTransaction? transaction = null,
            Func<DbParameter, DbParameter>? materializeParameter = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            AttachParameters(command.Parameters, parameters, materializeParameter);
            return command;
        }

        public static void AttachParameters(
            DbParameterCollection target,
            IReadOnlyList<DbParameter>? parameters,
            Func<DbParameter, DbParameter>? materializeParameter = null)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (parameters == null || parameters.Count == 0)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                target.Add(materializeParameter?.Invoke(parameter) ?? parameter);
            }
        }

        public static async Task<int> ExecuteNonQueryAsync(
            DbConnection connection,
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            CancellationToken token,
            DbTransaction? transaction = null,
            Func<DbParameter, DbParameter>? materializeParameter = null)
        {
            await using var command = CreateCommand(connection, sql, parameters, transaction, materializeParameter);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        public static async Task<object?> ExecuteScalarAsync(
            DbConnection connection,
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            CancellationToken token,
            DbTransaction? transaction = null,
            Func<DbParameter, DbParameter>? materializeParameter = null)
        {
            await using var command = CreateCommand(connection, sql, parameters, transaction, materializeParameter);
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        }

        public static async Task<DbDataReader> ExecuteOwnedReaderAsync(
            DbConnection ownedConnection,
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            CancellationToken token,
            DbTransaction? transaction = null,
            Func<DbParameter, DbParameter>? materializeParameter = null,
            CommandBehavior behavior = CommandBehavior.CloseConnection)
        {
            ArgumentNullException.ThrowIfNull(ownedConnection);

            DbCommand? command = null;
            try
            {
                command = CreateCommand(ownedConnection, sql, parameters, transaction, materializeParameter);
                var reader = await command.ExecuteReaderAsync(behavior, token).ConfigureAwait(false);
                return new OwnedDbDataReader(reader, command);
            }
            catch
            {
                if (command != null)
                {
                    await command.DisposeAsync().ConfigureAwait(false);
                }

                await ownedConnection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
