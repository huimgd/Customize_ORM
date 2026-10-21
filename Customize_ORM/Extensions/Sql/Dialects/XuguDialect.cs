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
        /// CLR 值类型转 System.Data.DbType，驱动内部再把 DbType 映射为最接近的 XGDbType。
        /// </summary>
        /// <param name="type">参数值 CLR 类型（已去除 Nullable 包装）。</param>
        private static DbType ResolveDbType(Type type)
        {
            if (type == typeof(ulong))
            {
                // ulong 超过 Int64 范围的值无法用 BIGINT 承载，按 DECIMAL 绑定
                return DbType.Decimal;
            }

            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => DbType.Boolean,
                TypeCode.SByte => DbType.SByte,
                TypeCode.Byte => DbType.Byte,
                TypeCode.Int16 => DbType.Int16,
                TypeCode.UInt16 => DbType.Int32,
                TypeCode.Int32 => DbType.Int32,
                TypeCode.UInt32 => DbType.Int64,
                TypeCode.Int64 => DbType.Int64,
                TypeCode.Single => DbType.Single,
                TypeCode.Double => DbType.Double,
                TypeCode.Decimal => DbType.Decimal,
                TypeCode.DateTime => DbType.DateTime,
                TypeCode.Char => DbType.String,
                TypeCode.String => DbType.String,
                _ when type == typeof(Guid) => DbType.Guid,
                _ when type == typeof(byte[]) => DbType.Binary,
                _ => DbType.Object
            };
        }
    }
}
