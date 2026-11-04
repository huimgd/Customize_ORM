using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Interfaces.Sql
{
    /// <summary>
    /// 数据库方言契约：负责 SQL 片段的语法差异（分页、类型转换、字面量渲染）与参数对象创建。
    /// 每个数据库实现一份，<see cref="Customize_ORM.Extensions.Sql.SqlDialect.For"/> 是唯一的解析入口。
    /// </summary>
    public interface IQueryDialect
    {
        /// <summary>
        /// 当前方言对应的数据库类型。
        /// </summary>
        DBType DbType { get; }

        /// <summary>
        /// 参数占位符前缀（PostgreSQL/ClickHouse/Oracle 风格为 @，虚谷为 :）。
        /// </summary>
        string ParameterPrefix { get; }

        /// <summary>
        /// 构建 LIMIT 子句。
        /// </summary>
        /// <param name="limit">最多返回行数，必须大于 0</param>
        string BuildLimitClause(int limit);

        /// <summary>
        /// 构建分页子句。
        /// </summary>
        /// <param name="pageNumber">页码，从 1 开始</param>
        /// <param name="pageSize">每页数据量，必须大于 0</param>
        string BuildPaginationClause(int pageNumber, int pageSize);

        /// <summary>
        /// 将标量 SQL 片段渲染为目标数据库的字符串表达式。
        /// </summary>
        /// <param name="sqlExpression">待转换的 SQL 片段</param>
        /// <param name="valueType">原始 CLR 类型</param>
        string RenderToString(string sqlExpression, Type valueType);

        /// <summary>
        /// 将任意 SQL 片段包装为 TRIM 调用。
        /// </summary>
        /// <param name="sqlExpression">待处理的 SQL 片段</param>
        string RenderTrim(string sqlExpression);

        /// <summary>
        /// 将任意 SQL 片段包装为 LOWER 调用。
        /// </summary>
        /// <param name="sqlExpression">待处理的 SQL 片段</param>
        string RenderLower(string sqlExpression);

        /// <summary>
        /// 将任意 SQL 片段包装为 UPPER 调用。
        /// </summary>
        /// <param name="sqlExpression">待处理的 SQL 片段</param>
        string RenderUpper(string sqlExpression);

        /// <summary>
        /// 将参数值渲染为 SQL 字面量，用于无法参数化的场景（如 LIMIT/常量折叠）。
        /// </summary>
        /// <param name="value">参数值</param>
        string RenderLiteral(object? value);

        /// <summary>
        /// 规范化参数值，便于特定驱动绑定。
        /// </summary>
        /// <param name="value">原始参数值</param>
        object? NormalizeParameterValue(object? value);

        /// <summary>
        /// 创建参数实例，并校验返回名称与请求占位符一致。
        /// </summary>
        /// <param name="name">参数名（含前缀）</param>
        /// <param name="value">参数值</param>
        /// <param name="propertyType">实体属性 CLR 类型，供数组/JSON 等特殊类型的参数工厂选择绑定方式</param>
        DbParameter CreateParameter(string name, object? value, Type propertyType);

        /// <summary>
        /// 构建"按主键集合删除"的语句。
        /// 各库语法差异明显：PostgreSQL 可用数组参数 <c>ANY(@ids)</c>，虚谷需展开为 <c>IN (:id0, :id1, ...)</c>。
        /// </summary>
        /// <param name="tableName">目标表名</param>
        /// <param name="keyColumn">主键列名</param>
        /// <param name="keyValues">主键值集合，不能为空</param>
        /// <param name="keyType">主键值的 CLR 类型</param>
        /// <param name="parameters">输出：本语句需要绑定的参数集合</param>
        string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters);
    }
}
