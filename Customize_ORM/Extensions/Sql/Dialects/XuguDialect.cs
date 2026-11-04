using Customize_ORM.Enums;
using System.Data;
using System.Data.Common;
using XuguClient;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 虚谷数据库方言：SQL 层支持 ":name" 命名占位符，驱动参数对象为 XGParameters。
    /// </summary>
    public sealed class XuguDialect : SqlDialect
    {
        public override DBType DbType => DBType.Xugu;

        public override string ParameterPrefix => ":";

        /// <summary>
        /// 虚谷无 char 通用转换语义，统一转为 VARCHAR。
        /// </summary>
        public override string RenderToString(string sqlExpression, Type valueType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            ArgumentNullException.ThrowIfNull(valueType);
            return $"CAST({sqlExpression} AS VARCHAR)";
        }

        /// <summary>
        /// 创建虚谷驱动参数对象。XGParameters(string, object) 构造不会推断参数类型（DbType 保留 Object），
        /// 这里按参数值运行时类型显式设置 DbType，规避驱动绑定时按 Object 处理的风险；null 值无法推断，保留默认。
        /// </summary>
        protected override DbParameter CreateParameterCore(string name, object? value)
        {
            var parameter = new XGParameters(name, value ?? DBNull.Value);
            if (value != null && value != DBNull.Value)
            {
                parameter.DbType = ResolveDbType(Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType());
            }

            return parameter;
        }

        /// <summary>
        /// 虚谷不支持数组参数，主键集合展开为 IN (:key0, :key1, ...)。
        /// </summary>
        /// <param name="tableName">目标表名</param>
        /// <param name="keyColumn">主键列名</param>
        /// <param name="keyValues">主键值集合</param>
        /// <param name="keyType">主键值的 CLR 类型</param>
        /// <param name="parameters">输出参数集合</param>
        public override string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
            ArgumentNullException.ThrowIfNull(keyValues);
            ArgumentNullException.ThrowIfNull(keyType);

            if (keyValues.Count == 0)
            {
                throw new ArgumentException("Key values cannot be empty.", nameof(keyValues));
            }

            parameters = new List<DbParameter>(keyValues.Count);
            var placeholders = new List<string>(keyValues.Count);
            for (var i = 0; i < keyValues.Count; i++)
            {
                var parameterName = $":{keyColumn}{i}";
                parameters.Add(XuguParameterFactory.Create(parameterName, keyValues[i], keyType));
                placeholders.Add(parameterName);
            }

            return $"DELETE FROM {tableName} WHERE {keyColumn} IN ({string.Join(", ", placeholders)})";
        }

        /// <summary>
        /// CLR 值类型转 System.Data.DbType，驱动内部再把 DbType 映射为最接近的 XGDbType。
        /// </summary>
        /// <param name="type">参数值 CLR 类型（已去除 Nullable 包装）。</param>
        private static System.Data.DbType ResolveDbType(Type type)
        {
            if (type == typeof(ulong))
            {
                // ulong 超过 Int64 范围的值无法用 BIGINT 承载，按 DECIMAL 绑定
                return System.Data.DbType.Decimal;
            }

            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => System.Data.DbType.Boolean,
                TypeCode.SByte => System.Data.DbType.SByte,
                TypeCode.Byte => System.Data.DbType.Byte,
                TypeCode.Int16 => System.Data.DbType.Int16,
                TypeCode.UInt16 => System.Data.DbType.Int32,
                TypeCode.Int32 => System.Data.DbType.Int32,
                TypeCode.UInt32 => System.Data.DbType.Int64,
                TypeCode.Int64 => System.Data.DbType.Int64,
                TypeCode.Single => System.Data.DbType.Single,
                TypeCode.Double => System.Data.DbType.Double,
                TypeCode.Decimal => System.Data.DbType.Decimal,
                TypeCode.DateTime => System.Data.DbType.DateTime,
                TypeCode.Char => System.Data.DbType.String,
                TypeCode.String => System.Data.DbType.String,
                _ when type == typeof(Guid) => System.Data.DbType.Guid,
                _ when type == typeof(byte[]) => System.Data.DbType.Binary,
                _ => System.Data.DbType.Object
            };
        }
    }
}
