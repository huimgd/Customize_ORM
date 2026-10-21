using System.Data;
using System.Data.Common;
using System.Text.Json;
using XuguClient;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 虚谷参数工厂：按实体属性 CLR 类型归一化标量值（null/枚举/ulong/JSON）并创建 XGParameters。
    /// </summary>
    internal static class XuguParameterFactory
    {
        /// <summary>
        /// 创建绑定参数；数组属性与 DDL 层（数组列映射 TEXT）一致，统一序列化为 JSON 文本。
        /// </summary>
        /// <param name="name">参数名（含 : 前缀）。</param>
        /// <param name="value">参数原始值。</param>
        /// <param name="propertyType">实体属性 CLR 类型。</param>
        public static DbParameter Create(string name, object? value, Type propertyType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(propertyType);

            var normalizedType = XuguTypeMapper.NormalizeClrType(propertyType);

            // XGParameters(string, object) 构造不推断参数类型（DbType 保留 Object），
            // 需按归一化后的值类型显式设置 DbType，规避驱动按 Object 绑定的风险
            var parameter = new XGParameters(name, NormalizeScalar(value, normalizedType))
            {
                DbType = ResolveDbType(normalizedType)
            };
            return parameter;
        }

        /// <summary>
        /// 与 XuguTypeMapper.NormalizeClrType 配套的写库值归一化：null→DBNull、枚举→底层类型、
        /// ulong→定点（超出 BIGINT 范围）、JSON→字符串、其余双向可转换的值做类型收敛。
        /// </summary>
        /// <param name="value">原始标量值。</param>
        /// <param name="propertyType">已去除 Nullable/枚举包装的属性类型。</param>
        private static object NormalizeScalar(object? value, Type propertyType)
        {
            if (value == null)
            {
                return DBNull.Value;
            }

            if (value is JsonElement or JsonDocument)
            {
                // JSON 统一以文本下发，由虚谷 JSON 列自行解析
                return ToJsonString(value);
            }

            if (value.GetType().IsArray && value.GetType() != typeof(byte[]))
            {
                // 数组属性与 DDL 层 TEXT 列对齐：统一 JSON 文本承载
                return JsonSerializer.Serialize(value);
            }

            var targetType = propertyType == typeof(ulong) ? typeof(decimal) : propertyType;
            if (value.GetType() == targetType)
            {
                return value;
            }

            return Convert.ChangeType(value, targetType);
        }

        /// <summary>
        /// JSON 值转字符串；JsonElement 为 null 字面量时 GetRawText 返回 "null"，属于合法 JSON 值，直接下发。
        /// </summary>
        private static string ToJsonString(object value)
        {
            return value switch
            {
                JsonElement jsonElement => jsonElement.GetRawText(),
                JsonDocument jsonDocument => jsonDocument.RootElement.GetRawText(),
                _ => throw new InvalidOperationException("Unexpected JSON value type.")
            };
        }

        /// <summary>
        /// CLR 值类型转 System.Data.DbType，驱动内部再把 DbType 映射为最接近的 XGDbType。
        /// </summary>
        /// <param name="type">参数值 CLR 类型（已去除 Nullable/枚举包装）。</param>
        private static DbType ResolveDbType(Type type)
        {
            if (type == typeof(ulong))
            {
                // ulong 超出 Int64 范围的值无法用 BIGINT 承载，按 DECIMAL 绑定
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
                TypeCode.Char or TypeCode.String => DbType.String,
                _ when type == typeof(Guid) => DbType.Guid,
                _ when type == typeof(byte[]) => DbType.Binary,
                _ when type.IsArray => DbType.String,
                _ when type == typeof(TimeSpan) => DbType.Time,
                _ when type == typeof(JsonElement) || type == typeof(JsonDocument) => DbType.String,
                _ => DbType.Object
            };
        }
    }
}
