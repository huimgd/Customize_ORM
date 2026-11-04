using Customize_ORM.Interfaces.Sql;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// PostgreSQL 参数工厂，统一处理 null、枚举、ulong、数组和批量数组参数的类型归一化。
    /// </summary>
    internal static class PostgreSqlParameterFactory
    {
        /// <summary>
        /// 对外以 <see cref="IParameterFactory"/> 形态暴露的实例。
        /// </summary>
        public static IParameterFactory Factory { get; } = new InstanceAdapter();

        /// <summary>
        /// 创建单个绑定参数。
        /// </summary>
        public static NpgsqlParameter Create(string name, object? value, Type propertyType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(propertyType);

            if (propertyType == typeof(byte[]))
            {
                return new NpgsqlParameter(name, value ?? DBNull.Value);
            }

            if (propertyType.IsArray)
            {
                return CreateArrayParameter(name, value, propertyType);
            }

            return new NpgsqlParameter(name, NormalizeScalar(value));
        }

        public static NpgsqlParameter CreateBulkArray(string name, IReadOnlyList<object?> values, Type propertyType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(propertyType);

            if (propertyType.IsArray && propertyType != typeof(byte[]))
            {
                // 二维数组参数不适合直接传给 unnest，这里转成 PostgreSQL 数组字面量集合。
                var arrayElementType = propertyType.GetElementType() ?? typeof(object);
                var formattedValues = new string[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    formattedValues[i] = FormatArrayLiteral(values[i] as Array);
                }

                return new NpgsqlParameter(name, formattedValues)
                {
                    NpgsqlDbType = NpgsqlDbType.Array | PostgreSqlTypeMapper.GetNpgsqlDbType(arrayElementType)
                };
            }

            var normalizedElementType = GetBulkScalarElementType(propertyType);
            var result = Array.CreateInstance(normalizedElementType, values.Count);
            for (var i = 0; i < values.Count; i++)
            {
                var normalizedValue = NormalizeBulkScalar(values[i], normalizedElementType);
                result.SetValue(normalizedValue, i);
            }

            return new NpgsqlParameter(name, result)
            {
                NpgsqlDbType = NpgsqlDbType.Array | PostgreSqlTypeMapper.GetNpgsqlDbType(normalizedElementType)
            };
        }

        private static NpgsqlParameter CreateArrayParameter(string name, object? value, Type propertyType)
        {
            var elementType = propertyType.GetElementType() ?? typeof(object);
            var normalizedElementType = NormalizeElementType(elementType);
            var normalizedValue = NormalizeArray(value, normalizedElementType);

            return new NpgsqlParameter(name, normalizedValue)
            {
                DbType = DbType.Object
            };
        }

        private static object NormalizeScalar(object? value)
        {
            if (value == null)
            {
                return DBNull.Value;
            }

            var runtimeType = value.GetType();
            if (runtimeType.IsEnum)
            {
                return Convert.ChangeType(value, Enum.GetUnderlyingType(runtimeType));
            }

            return value switch
            {
                ulong ulongValue => (decimal)ulongValue,
                _ => value
            };
        }

        private static object? NormalizeBulkScalar(object? value, Type targetType)
        {
            if (value == null || value == DBNull.Value)
            {
                return null;
            }

            return targetType == typeof(decimal) && value is ulong ulongValue
                ? (decimal)ulongValue
                : value.GetType().IsEnum
                    ? Convert.ChangeType(value, targetType)
                    : value;
        }

        private static Array NormalizeArray(object? value, Type elementType)
        {
            if (value is not Array arrayValue || arrayValue.Length == 0)
            {
                return Array.CreateInstance(elementType, 0);
            }

            return value switch
            {
                ulong[] ulongArray => Array.ConvertAll(ulongArray, item => (decimal)item),
                _ when arrayValue.GetType().GetElementType()?.IsEnum == true => ConvertEnumArray(arrayValue, elementType),
                _ => arrayValue
            };
        }

        private static Array ConvertEnumArray(Array arrayValue, Type elementType)
        {
            var converted = Array.CreateInstance(elementType, arrayValue.Length);
            for (var i = 0; i < arrayValue.Length; i++)
            {
                var item = arrayValue.GetValue(i);
                converted.SetValue(item == null ? null : Convert.ChangeType(item, elementType), i);
            }

            return converted;
        }

        private static Type NormalizeElementType(Type type)
        {
            var underlyingNullableType = Nullable.GetUnderlyingType(type);
            if (underlyingNullableType != null)
            {
                type = underlyingNullableType;
            }

            return type.IsEnum ? Enum.GetUnderlyingType(type) : type;
        }

        private static string FormatArrayLiteral(Array? arrayValue)
        {
            if (arrayValue == null || arrayValue.Length == 0)
            {
                return "{}";
            }

            var elements = new List<string>(arrayValue.Length);
            foreach (var item in arrayValue)
            {
                if (item == null || item == DBNull.Value)
                {
                    elements.Add("NULL");
                    continue;
                }

                var element = item switch
                {
                    ulong ulongValue => ((decimal)ulongValue).ToString(),
                    Enum enumValue => Convert.ChangeType(enumValue, Enum.GetUnderlyingType(enumValue.GetType()))?.ToString() ?? string.Empty,
                    _ => item.ToString() ?? string.Empty
                };

                element = element.Replace("\"", "\"\"");
                elements.Add($"\"{element}\"");
            }

            return "{" + string.Join(",", elements) + "}";
        }

        private static Type GetBulkScalarElementType(Type propertyType)
        {
            var normalizedType = PostgreSqlTypeMapper.NormalizeClrType(propertyType);
            return normalizedType == typeof(ulong)
                ? typeof(decimal)
                : normalizedType;
        }

        /// <summary>
        /// 静态工具方法到 <see cref="IParameterFactory"/> 的适配，供方言层统一调用。
        /// </summary>
        private sealed class InstanceAdapter : IParameterFactory
        {
            public DbParameter Create(string name, object? value, Type propertyType)
                => PostgreSqlParameterFactory.Create(name, value, propertyType);

            public DbParameter CreateBulkArray(string name, IReadOnlyList<object?> values, Type propertyType)
                => PostgreSqlParameterFactory.CreateBulkArray(name, values, propertyType);
        }
    }
}
