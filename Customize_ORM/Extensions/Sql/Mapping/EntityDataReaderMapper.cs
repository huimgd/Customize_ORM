using System.Collections.Concurrent;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 实体行映射器，根据 ORM 映射元数据缓存列到属性的赋值委托，减少重复反射开销。
    /// </summary>
    internal static class EntityDataReaderMapper<T> where T : class
    {
        private static readonly ConcurrentDictionary<EntityMapping<T>, Func<DbDataReader, T>> Cache = new();

        public static Func<DbDataReader, T> Create(EntityMapping<T> mapping)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            return Cache.GetOrAdd(mapping, BuildMapper);
        }

        private static Func<DbDataReader, T> BuildMapper(EntityMapping<T> mapping)
        {
            var orderedBindings = BuildOrderedBindings(mapping);
            return reader =>
            {
                var entity = Activator.CreateInstance<T>();
                for (var i = 0; i < orderedBindings.Length && i < reader.FieldCount; i++)
                {
                    var binding = orderedBindings[i];
                    if (binding.Setter == null)
                    {
                        continue;
                    }

                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    var converted = DbRecordMapper.ConvertToType(value, binding.PropertyType);
                    binding.Setter(entity, converted!);
                }

                return entity;
            };
        }

        private static (Action<T, object>? Setter, Type PropertyType)[] BuildOrderedBindings(EntityMapping<T> mapping)
        {
            var setters = new (Action<T, object>? Setter, Type PropertyType)[mapping.AllColumns.Length];
            if (mapping.ValueSetters == null)
            {
                return setters;
            }

            for (var i = 0; i < mapping.AllColumns.Length; i++)
            {
                if (mapping.ValueSetters.TryGetValue(mapping.AllColumns[i], out var setter) &&
                    mapping.PropertyMap.TryGetValue(mapping.AllColumns[i], out var property))
                {
                    setters[i] = (setter, property.PropertyType);
                }
            }

            return setters;
        }
    }
}
