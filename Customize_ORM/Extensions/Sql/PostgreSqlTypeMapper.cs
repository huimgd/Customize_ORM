using NpgsqlTypes;

namespace Customize_ORM.Extensions.Sql
{
    internal static class PostgreSqlTypeMapper
    {
        public static string GetPostgreSqlArrayType(Type elementType)
        {
            var normalizedType = NormalizeClrType(elementType);

            return Type.GetTypeCode(normalizedType) switch
            {
                TypeCode.Boolean => "boolean[]",
                TypeCode.Byte => "smallint[]",
                TypeCode.SByte => "smallint[]",
                TypeCode.Char => "char[]",
                TypeCode.DateTime => "timestamp[]",
                TypeCode.Decimal => "numeric[]",
                TypeCode.Double => "double precision[]",
                TypeCode.Int16 => "smallint[]",
                TypeCode.Int32 => "integer[]",
                TypeCode.Int64 => "bigint[]",
                TypeCode.Single => "real[]",
                TypeCode.String => "text[]",
                TypeCode.UInt16 => "integer[]",
                TypeCode.UInt32 => "bigint[]",
                TypeCode.UInt64 => "numeric[]",
                _ when normalizedType == typeof(Guid) => "uuid[]",
                _ when normalizedType == typeof(byte[]) => "bytea[]",
                _ when normalizedType == typeof(TimeSpan) => "interval[]",
                _ when normalizedType == typeof(DateTimeOffset) => "timestamptz[]",
                _ => "text[]"
            };
        }

        public static Type NormalizeClrType(Type propertyType)
        {
            ArgumentNullException.ThrowIfNull(propertyType);

            var nullableType = Nullable.GetUnderlyingType(propertyType);
            if (nullableType != null)
            {
                propertyType = nullableType;
            }

            return propertyType.IsEnum
                ? Enum.GetUnderlyingType(propertyType)
                : propertyType;
        }

        public static NpgsqlDbType GetNpgsqlDbType(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            var normalizedType = NormalizeClrType(type);
            return Type.GetTypeCode(normalizedType) switch
            {
                TypeCode.Boolean => NpgsqlDbType.Boolean,
                TypeCode.Byte => NpgsqlDbType.Smallint,
                TypeCode.Char => NpgsqlDbType.Char,
                TypeCode.DateTime => NpgsqlDbType.Timestamp,
                TypeCode.Decimal => NpgsqlDbType.Numeric,
                TypeCode.Double => NpgsqlDbType.Double,
                TypeCode.Int16 => NpgsqlDbType.Smallint,
                TypeCode.Int32 => NpgsqlDbType.Integer,
                TypeCode.Int64 => NpgsqlDbType.Bigint,
                TypeCode.SByte => NpgsqlDbType.Smallint,
                TypeCode.Single => NpgsqlDbType.Real,
                TypeCode.String => NpgsqlDbType.Text,
                TypeCode.UInt16 => NpgsqlDbType.Integer,
                TypeCode.UInt32 => NpgsqlDbType.Bigint,
                TypeCode.UInt64 => NpgsqlDbType.Numeric,
                _ when normalizedType == typeof(Guid) => NpgsqlDbType.Uuid,
                _ when normalizedType == typeof(byte[]) => NpgsqlDbType.Bytea,
                _ when normalizedType == typeof(string[]) => NpgsqlDbType.Text,
                _ when normalizedType == typeof(ulong) => NpgsqlDbType.Numeric,
                _ when normalizedType == typeof(long) => NpgsqlDbType.Bigint,
                _ when normalizedType == typeof(TimeSpan) => NpgsqlDbType.Interval,
                _ when normalizedType == typeof(DateTimeOffset) => NpgsqlDbType.TimestampTz,
                _ when normalizedType.FullName == "System.Text.Json.JsonElement" => NpgsqlDbType.Jsonb,
                _ => NpgsqlDbType.Text
            };
        }
    }
}
