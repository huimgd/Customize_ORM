using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Maps data-reader rows to scalar values or simple DTOs without a provider-specific micro-ORM dependency.
    /// </summary>
    internal static class DbRecordMapper
    {
        private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> PropertyMapCache = new();

        public static T MapSingle<T>(DbDataReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            if (reader.FieldCount == 0)
            {
                return default!;
            }

            var targetType = typeof(T);
            if (CanReadAsScalar(targetType))
            {
                return MapValue<T>(reader.IsDBNull(0) ? null : reader.GetValue(0));
            }

            return MapObject<T>(reader);
        }

        public static T MapValue<T>(object? value)
        {
            return (T)ConvertToPropertyType(value, typeof(T))!;
        }

        public static object? ConvertToType(object? value, Type targetType)
        {
            ArgumentNullException.ThrowIfNull(targetType);
            return ConvertToPropertyType(value, targetType);
        }

        private static T MapObject<T>(DbDataReader reader)
        {
            object instance = Activator.CreateInstance(typeof(T))
                ?? throw new InvalidOperationException($"Could not create an instance of {typeof(T).Name}.");

            var propertyMap = PropertyMapCache.GetOrAdd(typeof(T), BuildPropertyMap);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnKey = NormalizeName(reader.GetName(i));
                if (!propertyMap.TryGetValue(columnKey, out var property))
                {
                    continue;
                }

                var rawValue = reader.IsDBNull(i) ? null : reader.GetValue(i);
                property.SetValue(instance, ConvertToPropertyType(rawValue, property.PropertyType));
            }

            return (T)instance;
        }

        private static Dictionary<string, PropertyInfo> BuildPropertyMap(Type type)
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanWrite)
                {
                    continue;
                }

                map[NormalizeName(property.Name)] = property;
            }

            return map;
        }

        private static bool CanReadAsScalar(Type type)
        {
            var underlyingType = Nullable.GetUnderlyingType(type) ?? type;
            return underlyingType.IsPrimitive
                || underlyingType.IsEnum
                || underlyingType == typeof(string)
                || underlyingType == typeof(decimal)
                || underlyingType == typeof(Guid)
                || underlyingType == typeof(DateTime)
                || underlyingType == typeof(DateTimeOffset)
                || underlyingType == typeof(TimeSpan)
                || underlyingType == typeof(byte[]);
        }

        private static string NormalizeName(string name)
        {
            return name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        }

        private static object? ConvertToPropertyType(object? value, Type targetType)
        {
            if (value == null || value == DBNull.Value)
            {
                if (Nullable.GetUnderlyingType(targetType) != null || !targetType.IsValueType)
                {
                    return null;
                }

                return Activator.CreateInstance(targetType);
            }

            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlyingType.IsInstanceOfType(value))
            {
                return value;
            }

            if (underlyingType.IsEnum)
            {
                if (value is string enumName)
                {
                    return Enum.Parse(underlyingType, enumName, ignoreCase: true);
                }

                var enumBaseType = Enum.GetUnderlyingType(underlyingType);
                var enumBaseValue = Convert.ChangeType(value, enumBaseType);
                return Enum.ToObject(underlyingType, enumBaseValue!);
            }

            if (underlyingType == typeof(Guid))
            {
                return value is Guid guid ? guid : Guid.Parse(value.ToString()!);
            }

            if (underlyingType == typeof(DateTimeOffset))
            {
                return value switch
                {
                    DateTimeOffset dto => dto,
                    DateTime dt => new DateTimeOffset(dt),
                    _ => DateTimeOffset.Parse(value.ToString()!)
                };
            }

            if (underlyingType == typeof(TimeSpan))
            {
                return value is TimeSpan timeSpan ? timeSpan : TimeSpan.Parse(value.ToString()!);
            }

            return Convert.ChangeType(value, underlyingType);
        }
    }
}
