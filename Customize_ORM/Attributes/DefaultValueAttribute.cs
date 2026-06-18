namespace Customize_ORM.Attributes
{
    /// <summary>
    /// 默认值特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
    public class DefaultValueAttribute : Attribute
    {
        public object Value { get; protected set; }
        public Type ValueType { get; protected set; }
        public bool OnlyUpdate { get; protected set; }

        public DefaultValueAttribute(object value, bool onlyUpdate = true)
        {
            Value = value;
            ValueType = value.GetType();
            OnlyUpdate = onlyUpdate;
        }

        /// <summary>
        /// 获取指定类型的默认值
        /// </summary>
        public T? GetValue<T>()
        {
            if (Value == null) return default;

            // 处理特殊类型
            if (typeof(T) == typeof(DateTimeOffset))
            {
                if (Value is string strValue)
                {
                    if (strValue.ToLower() == "now")
                        return (T)(object)DateTimeOffset.Now;
                    if (strValue.ToLower() == "utcnow")
                        return (T)(object)DateTimeOffset.UtcNow;
                }
            }

            try
            {
                return (T)Convert.ChangeType(Value, typeof(T));
            }
            catch
            {
                return default;
            }
        }
    }
}
