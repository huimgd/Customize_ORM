using System.Text.Json;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 虚谷类型工具，集中维护 CLR 类型与虚谷 DDL 列类型的对应关系，主要供建 staging/临时表使用。
    /// </summary>
    internal static class XuguTypeMapper
    {
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

        /// <summary>
        /// CLR 属性类型转虚谷 DDL 列类型，用于批量更新 staging/临时表建表。
        /// </summary>
        /// <param name="propertyType">实体属性 CLR 类型。</param>
        public static string GetXuguColumnType(Type propertyType)
        {
            var normalizedType = NormalizeClrType(propertyType);

            if (normalizedType.IsArray)
            {
                return "TEXT";
            }

            if (normalizedType == typeof(JsonElement) || normalizedType == typeof(JsonDocument))
            {
                return "JSON";
            }

            if (normalizedType == typeof(byte[]))
            {
                return "BLOB";
            }

            if (normalizedType == typeof(Guid))
            {
                return "GUID";
            }

            if (normalizedType == typeof(DateTimeOffset))
            {
                return $"VARCHAR({DateTimeOffsetMaxChars})";
            }

            if (normalizedType == typeof(TimeSpan))
            {
                return "TIME";
            }

            return Type.GetTypeCode(normalizedType) switch
            {
                TypeCode.Boolean => "BOOLEAN",
                TypeCode.SByte or TypeCode.Byte => "TINYINT",
                TypeCode.Int16 => "SMALLINT",
                TypeCode.UInt16 => "INTEGER",
                TypeCode.Int32 => "INTEGER",
                TypeCode.UInt32 => "BIGINT",
                TypeCode.Int64 => "BIGINT",
                TypeCode.Single => "REAL",
                TypeCode.Double => "DOUBLE",
                TypeCode.UInt64 => "NUMERIC",
                TypeCode.Decimal => $"NUMERIC({NumericPrecision}, {NumericScale})",
                TypeCode.DateTime => "DATETIME",
                TypeCode.Char or TypeCode.String => $"VARCHAR({StringMaxChars})",
                _ => $"VARCHAR({StringMaxChars})"
            };
        }

        /// <summary>
        /// 虚谷 NUMERIC 与 DECIMAL 不区分精度语义，建 staging 表时统一用 NUMERIC(p,s)。
        /// </summary>
        public const int NumericPrecision = 38;
        public const int NumericScale = 12;
        public const int StringMaxChars = 4000;
        public const int DateTimeOffsetMaxChars = 64;
    }
}
