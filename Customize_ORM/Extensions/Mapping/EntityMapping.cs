using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// 实体映射
    /// </summary>
    public class EntityMapping<T>
    {
        /// <summary>
        /// 所有字段
        /// </summary>
        public string[] AllColumns { get; set; } = [];

        /// <summary>
        /// 索引字段
        /// </summary>
        public string Key { get; set; } = "id";

        /// <summary>
        /// 分区字段
        /// </summary>
        public string PartitionKey { get; set; } = string.Empty;

        public Dictionary<string, PropertyInfo> PropertyMap { get; set; } = new();

        /// <summary>
        /// 值获取
        /// </summary>
        public Dictionary<string, Func<T, object?>> ValueGetters { get; set; } = new();

        /// <summary>
        /// 默认值获取
        /// </summary>
        public Dictionary<string, Func<object?>> DefaultValueGetters { get; set; } = new();

        /// <summary>
        /// jsonb 字段集合，用于 COPY 写入前识别需要 JSON 序列化的字段。
        /// </summary>
        public HashSet<string> JsonbColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 值设定
        /// </summary>
        public Dictionary<string, Action<T, object>>? ValueSetters { get; set; }

        private string _tableName = string.Empty;

        public string TableName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_tableName))
                {
                    var type = typeof(T);
                    if (Attribute.GetCustomAttribute(type, typeof(TableAttribute), true) is TableAttribute attribute && !string.IsNullOrEmpty(attribute.Name))
                    {
                        return attribute.Name;
                    }

                    _tableName = Extension.ToSnakeCase(type.Name);
                }

                return _tableName;
            }
        }

        /// <summary>
        /// 获取属性对应的数据库字段名。
        /// </summary>
        /// <param name="property">实体属性信息。</param>
        /// <returns>数据库字段名。</returns>
        public string GetColumnName(PropertyInfo property)
        {
            // 从 _entityMappingCache.PropertyMap 反向查找
            var pair = PropertyMap.FirstOrDefault(kvp => kvp.Value == property);
            if (pair.Key != null)
                return pair.Key;
            // 如果没有直接映射，则根据规则生成（与构造函数中的逻辑一致）
            var colAttr = property.GetCustomAttribute<ColumnAttribute>();
            return colAttr?.Name ?? Extension.ToSnakeCase(property.Name);
        }
    }
}
