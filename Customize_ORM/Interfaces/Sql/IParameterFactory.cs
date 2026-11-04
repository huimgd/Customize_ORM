using System.Data.Common;

namespace Customize_ORM.Interfaces.Sql
{
    /// <summary>
    /// 参数工厂契约：按实体属性 CLR 类型归一化取值并创建驱动参数对象。
    /// 各数据库的数组、JSON、枚举、ulong 等差异在此层落地。
    /// </summary>
    public interface IParameterFactory
    {
        /// <summary>
        /// 创建单个绑定参数。
        /// </summary>
        /// <param name="name">参数名（含占位符前缀）</param>
        /// <param name="value">参数原始值</param>
        /// <param name="propertyType">实体属性 CLR 类型</param>
        DbParameter Create(string name, object? value, Type propertyType);

        /// <summary>
        /// 为一组值创建批量数组参数（如 PostgreSQL 的 unnest 输入）。
        /// </summary>
        /// <param name="name">参数名（含占位符前缀）</param>
        /// <param name="values">同一列的多个取值</param>
        /// <param name="propertyType">实体属性 CLR 类型</param>
        DbParameter CreateBulkArray(string name, IReadOnlyList<object?> values, Type propertyType);
    }
}
