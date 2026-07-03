using Customize_ORM.Attributes; 
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Customize_ORM.Extensions
{
    public class ObjectMapping<T>
    {
        private static readonly EntityMapping<T> _updateMapping = BuildEntityMapping(isUpdate: true);
        private static readonly EntityMapping<T> _insertMapping = BuildEntityMapping(isUpdate: false);
        public static EntityMapping<T> MappingForUpdate => _updateMapping;
        public static EntityMapping<T> MappingForInsert => _insertMapping;
        public delegate object[] DRowFactory(T entity);
        public static readonly DRowFactory RowFactory = CreateRowFactory();
        private static DRowFactory CreateRowFactory()
        {
            var mapping = _insertMapping;
            var getterItems = mapping.ValueGetters?.ToArray() ?? [];

            return entity =>
            {
                var values = new object[getterItems.Length];
                for (int i = 0; i < getterItems.Length; i++)
                {
                    var columnName = getterItems[i].Key;
                    var value = getterItems[i].Value(entity);
                    values[i] = ConvertValueForWrite(value, mapping.JsonbColumns.Contains(columnName));
                }
                return values;
            };
        }
        private static EntityMapping<T> BuildEntityMapping(bool isUpdate = true)
        {
            var properties = typeof(T).GetProperties().Where(p => p.CanRead).ToList();
            var columnMap = new Dictionary<string, PropertyInfo>(properties.Count, StringComparer.OrdinalIgnoreCase);
            string key = "id";
            string? partitionKey = string.Empty;
            Dictionary<string, Func<T, object?>> valueGetters;
            Dictionary<string, Action<T, object>>? valueSetters;
            Dictionary<string, Func<object?>> defaultValueGetters = new Dictionary<string, Func<object?>>(StringComparer.OrdinalIgnoreCase);
            var jsonbColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties)
            {
                if (property.IsDefined(typeof(IgnoreAttribute), true))
                {
                    continue;
                }
                var columnName = (Attribute.GetCustomAttribute(property, typeof(ColumnAttribute), true) as ColumnAttribute)?.Name ?? Extension.ToSnakeCase(property.Name);
                columnMap[columnName] = property;
                if (IsJsonbColumn(property))
                {
                    jsonbColumns.Add(columnName);
                }
                var defaultValueAttr = property.GetCustomAttribute<DefaultValueAttribute>(true);
                if (defaultValueAttr != null)
                {
                    // 创建默认值获取器
                    if (defaultValueAttr.OnlyUpdate == isUpdate)
                    {
                        defaultValueGetters[columnName] = CreateDefaultValueGetter(property, defaultValueAttr);
                    }

                }
                if (property.IsDefined(typeof(IndexAttribute), true))
                {
                    key = columnName;
                }
                if (property.IsDefined(typeof(PartitionKeyAttribute), true))
                {
                    partitionKey = columnName;
                }
            }
            valueGetters = columnMap.ToDictionary(
              pair => pair.Key,
              pair => CreatePropertyGetter(pair.Value, defaultValueGetters.TryGetValue(pair.Key, out var defaultValueGetter) ? defaultValueGetter : null),
              StringComparer.OrdinalIgnoreCase
          );
            valueSetters = columnMap.ToDictionary(
               pair => pair.Key,
               pair => CreatePropertySetter(pair.Value),
               StringComparer.OrdinalIgnoreCase
           );
            return new EntityMapping<T>
            {
                AllColumns = columnMap.Keys.ToArray(),
                Key = key,
                PartitionKey = partitionKey,
                PropertyMap = columnMap,
                ValueGetters = valueGetters,
                ValueSetters = valueSetters,
                DefaultValueGetters = defaultValueGetters,
                JsonbColumns = jsonbColumns,
            };
        }

        /// <summary>
        /// 判断属性是否声明为 PostgreSQL jsonb 字段。
        /// </summary>
        /// <param name="property">实体属性信息。</param>
        /// <returns>声明为 jsonb 字段时返回 true。</returns>
        public static bool IsJsonbColumn(PropertyInfo property)
        {
            ArgumentNullException.ThrowIfNull(property);

            var columnAttr = property.GetCustomAttribute<ColumnAttribute>(true);
            if (string.Equals(columnAttr?.TypeName, "jsonb", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            return propertyType == typeof(JsonElement) || propertyType == typeof(JsonDocument);
        }

        /// <summary>
        /// 将实体属性值转换为 PostgreSQL COPY 可写入的值。
        /// </summary>
        /// <param name="value">原始属性值。</param>
        /// <param name="isJsonb">当前字段是否为 jsonb。</param>
        /// <returns>转换后的数据库写入值。</returns>
        public static object ConvertValueForWrite(object? value, bool isJsonb = false)
        {
            if (value is null)
            {
                return DBNull.Value;
            }

            if (isJsonb)
            {
                return ConvertJsonbValue(value);
            }

            return value switch
            {
                ulong ulongValue => (decimal)ulongValue,
                ulong[] ulongArray => Array.ConvertAll(ulongArray, x => (decimal)x),
                _ => value
            };
        }

        /// <summary>
        /// 将 jsonb 字段值转换为 Npgsql 可识别的 JSON 值。
        /// </summary>
        /// <param name="value">jsonb 字段原始值。</param>
        /// <returns>JsonElement 值。</returns>
        private static object ConvertJsonbValue(object value)
        {
            return value switch
            {
                JsonElement jsonElement => jsonElement.Clone(),
                JsonDocument jsonDocument => jsonDocument.RootElement.Clone(),
                string jsonText => JsonDocument.Parse(jsonText).RootElement.Clone(),
                _ => JsonSerializer.SerializeToElement(value)
            };
        }

        private static Func<T, object?> CreatePropertyGetter(PropertyInfo property, Func<object?>? defaultValueGetter)
        {
            var param = Expression.Parameter(typeof(T), "x");
            var prop = Expression.Property(param, property);
            var propertyType = prop.Type;
            Expression getValueExpr;
            if (propertyType.IsEnum)
            {
                // 获取枚举的基础类型（int, byte等）
                var underlyingType = Enum.GetUnderlyingType(propertyType);
                // 先转换为基础类型，再转换为object
                var convertToUnderlying = Expression.Convert(prop, underlyingType);
                getValueExpr = Expression.Convert(convertToUnderlying, typeof(object));
            }
            else
            {
                getValueExpr = Expression.Convert(prop, typeof(object));
            }
            // 如果有默认值获取器，创建条件表达式
            if (defaultValueGetter != null)
            {
                // 创建调用默认值获取器的表达式
                var defaultValueExpr = Expression.Constant(defaultValueGetter);
                var invokeDefaultValueExpr = Expression.Invoke(defaultValueExpr);

                // 判断属性值是否为null
                Expression condition;
                if (propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null)
                {
                    // 非可空值类型，不需要检查null
                    condition = Expression.Constant(false); // 总是返回属性值
                }
                else
                {
                    // 可空值类型或引用类型，检查是否为null
                    condition = Expression.Equal(prop, Expression.Constant(null, propertyType));
                }

                // 创建条件表达式：如果值为null，则使用默认值，否则使用属性值
                getValueExpr = Expression.Condition(
                    condition,
                    invokeDefaultValueExpr,
                    getValueExpr
                );
            }
            return Expression.Lambda<Func<T, object>>(getValueExpr, param).Compile();
        }
        private static Action<T, object> CreatePropertySetter(PropertyInfo property)
        {
            var entityParam = Expression.Parameter(typeof(T), "entity");
            var valueParam = Expression.Parameter(typeof(object), "value");

            // 处理Nullable类型
            var propertyType = property.PropertyType;
            var isNullable = propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>);
            var underlyingType = isNullable ? Nullable.GetUnderlyingType(propertyType) : propertyType;

            Expression convertedValue;
            if (underlyingType?.IsEnum == true)
            {
                // 处理枚举类型
                if (isNullable)
                {
                    // 处理Nullable<Enum>
                    // 表达式: value != null ? (EnumType?)Enum.ToObject(underlyingType, Convert.ToInt32(value)) : null
                    var nullConstant = Expression.Constant(null, propertyType);
                    var notNullCondition = Expression.NotEqual(valueParam, Expression.Constant(null, typeof(object)));

                    // 将object转换为int（枚举的基础类型），再转换为枚举类型
                    var convertToInt = Expression.Convert(valueParam, typeof(object));
                    var enumToObject = Expression.Call(typeof(Enum), "ToObject", null,
                        Expression.Constant(underlyingType), convertToInt);
                    var convertToNullable = Expression.Convert(enumToObject, propertyType);

                    convertedValue = Expression.Condition(notNullCondition, convertToNullable, nullConstant);
                }
                else
                {
                    // 处理非空枚举
                    var convertToInt = Expression.Convert(valueParam, typeof(object));
                    var enumToObject = Expression.Call(typeof(Enum), "ToObject", null,
                        Expression.Constant(underlyingType), convertToInt);
                    convertedValue = Expression.Convert(enumToObject, propertyType);
                }
            }
            else if (underlyingType == typeof(DateTimeOffset))
            {
                // 处理 DateTimeOffset 类型：数据库可能返回 DateTime，需显式转换
                // 处理 null 值（如果属性可空）
                Expression finalExpr;
                if (isNullable)
                {
                    var nullCondition = Expression.Equal(valueParam, Expression.Constant(null, typeof(object)));
                    var nullConstant = Expression.Constant(null, propertyType);
                    var nonNullExpr = BuildDateTimeOffsetConversion(valueParam, propertyType);
                    finalExpr = Expression.Condition(nullCondition, nullConstant, nonNullExpr);
                }
                else
                {
                    finalExpr = BuildDateTimeOffsetConversion(valueParam, propertyType);
                }
                convertedValue = finalExpr;
            }
            else
            {
                convertedValue = Expression.Convert(valueParam, propertyType);
            }

            var propertyAccess = Expression.Property(entityParam, property);
            var assignment = Expression.Assign(propertyAccess, convertedValue);

            return Expression.Lambda<Action<T, object>>(assignment, entityParam, valueParam).Compile();
        }
        private static Func<object?> CreateDefaultValueGetter(PropertyInfo property, DefaultValueAttribute defaultValueAttr)
        {
            return () =>
            {
                // 检查是否为 DateTimeDefaultValueAttribute
                if (defaultValueAttr is DateTimeDefaultValueAttribute dateTimeAttr)
                {
                    return dateTimeAttr.GetValue();
                }

                // 普通 DefaultValueAttribute
                var propertyType = property.PropertyType;
                var underlyingType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

                try
                {
                    // 尝试转换为目标类型
                    return Convert.ChangeType(defaultValueAttr.Value, underlyingType);
                }
                catch
                {
                    // 转换失败，返回默认值
                    return null;
                }
            };
        }


        // 辅助方法：构建 DateTime -> DateTimeOffset 或直接转换的表达式
        /// <summary>
        /// DateTime转换为DateTimeOffset
        /// </summary>
        /// <param name="valueParam"></param>
        /// <param name="targetType"></param>
        /// <returns></returns>
        private static Expression BuildDateTimeOffsetConversion(Expression valueParam, Type targetType)
        {
            // targetType 可能是 DateTimeOffset 或 DateTimeOffset?
            var nonNullableTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var isNullable = targetType != nonNullableTarget;

            // 判断值是否为 DateTime
            var isDateTimeTest = Expression.TypeIs(valueParam, typeof(DateTime));

            // 分支：如果是 DateTime，先拆箱成 DateTime，再转换为 DateTimeOffset（利用隐式转换）
            var dateTimeValue = Expression.Convert(valueParam, typeof(DateTime));
            var fromDateTimeExpr = Expression.Convert(dateTimeValue, nonNullableTarget);

            // 分支：如果不是 DateTime，尝试直接转换（可能已经是 DateTimeOffset，或其它可转换类型）
            var directConvertExpr = Expression.Convert(valueParam, nonNullableTarget);

            // 条件表达式：resultExpr 类型为 ConditionalExpression，但其基类是 Expression
            Expression resultExpr = Expression.Condition(isDateTimeTest, fromDateTimeExpr, directConvertExpr);

            // 如果目标类型是可空的，将结果包装为 Nullable<DateTimeOffset>
            if (isNullable)
            {
                // 注意：resultExpr 类型现在是 Expression，可以接收 UnaryExpression
                resultExpr = Expression.Convert(resultExpr, targetType);
            }
            return resultExpr;
        }

    }
}
