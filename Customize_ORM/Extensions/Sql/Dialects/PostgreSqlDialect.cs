using Npgsql;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    public sealed class PostgreSqlDialect : SqlDialect
    {
        public override string ParameterPrefix => "@";

        public override string BuildPaginationClause(int pageNumber, int pageSize)
        {
            if (pageNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be greater than zero.");
            }

            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
            }

            var offset = (pageNumber - 1) * pageSize;
            return $" LIMIT {pageSize} OFFSET {offset}";
        }

        protected override DbParameter CreateParameterCore(string name, object? value)
            => new NpgsqlParameter(name, value);
    }
}
