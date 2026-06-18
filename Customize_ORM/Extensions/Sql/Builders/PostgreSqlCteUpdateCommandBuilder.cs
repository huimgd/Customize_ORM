using Customize_ORM.Extensions;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// PostgreSQL CTE 批量更新命令构建器，负责生成 WITH/UPDATE SQL 与数组参数。
    /// </summary>
    internal sealed class PostgreSqlCteUpdateCommandBuilder<T>
    {
        private readonly EntityMapping<T> _mapping;

        public PostgreSqlCteUpdateCommandBuilder(EntityMapping<T> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        public PostgreSqlCteUpdateCommand Build(
            IReadOnlyList<T> entities,
            IReadOnlyList<string> columns,
            string tableName,
            string cteName,
            string? additionalWhereClause = null)
        {
            ArgumentNullException.ThrowIfNull(entities);
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(cteName);

            if (entities.Count == 0)
            {
                throw new ArgumentException("Entities cannot be empty.", nameof(entities));
            }

            if (columns.Count == 0)
            {
                throw new ArgumentException("Update columns cannot be empty.", nameof(columns));
            }

            if (string.IsNullOrWhiteSpace(_mapping.PartitionKey))
            {
                throw new InvalidOperationException($"{typeof(T).Name} must define a partition key for CTE bulk updates.");
            }

            string[] allColumns = [_mapping.Key, _mapping.PartitionKey, .. columns];
            var setClauses = columns.Select(column => $"{column} = {cteName}.{column}").ToArray();

            var whereConditions = new List<string>
            {
                $"{tableName}.{_mapping.Key} = {cteName}.{_mapping.Key}",
                $"{tableName}.{_mapping.PartitionKey} = {cteName}.{_mapping.PartitionKey}"
            };
            if (!string.IsNullOrWhiteSpace(additionalWhereClause))
            {
                whereConditions.Add(additionalWhereClause);
            }

            List<DbParameter> parameters = [];
            var selectColumns = new List<string>(allColumns.Length);
            foreach (var column in allColumns)
            {
                if (!_mapping.ValueGetters.TryGetValue(column, out var getter))
                {
                    throw new InvalidOperationException(
                        $"Column '{column}' does not exist in mapping for {typeof(T).Name}.");
                }

                if (!_mapping.PropertyMap.TryGetValue(column, out var property))
                {
                    throw new InvalidOperationException(
                        $"Property mapping for column '{column}' does not exist in {typeof(T).Name}.");
                }

                var values = new object?[entities.Count];
                for (var i = 0; i < entities.Count; i++)
                {
                    values[i] = getter(entities[i]);
                }

                var propertyType = property.PropertyType;
                var parameter = PostgreSqlParameterFactory.CreateBulkArray($"@{column}", values, propertyType);
                parameters.Add(parameter);
                selectColumns.Add(BuildSelectColumn(column, propertyType));
            }

            var sql = $@" WITH {cteName} AS (
                        SELECT {string.Join(", ", selectColumns)}
                        )UPDATE {tableName} 
                            SET {string.Join(", ", setClauses)}
                            FROM {cteName}
                            WHERE {string.Join(" AND ", whereConditions)}";

            return new PostgreSqlCteUpdateCommand(sql, parameters);
        }

        private static string BuildSelectColumn(string column, Type propertyType)
        {
            if (propertyType.IsArray && propertyType != typeof(byte[]))
            {
                var arrayElementType = propertyType.GetElementType() ?? typeof(object);
                var sqlArrayType = PostgreSqlTypeMapper.GetPostgreSqlArrayType(arrayElementType);
                return $"unnest(@{column})::{sqlArrayType} as {column}";
            }

            return $"unnest(@{column}) as {column}";
        }
    }

    /// <summary>
    /// CTE 更新命令定义，包含 SQL 文本和已经归一化的数据库参数。
    /// </summary>
    internal sealed record PostgreSqlCteUpdateCommand(string Sql, IReadOnlyList<DbParameter> Parameters);
}
