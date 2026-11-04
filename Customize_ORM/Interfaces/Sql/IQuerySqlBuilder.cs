namespace Customize_ORM.Interfaces.Sql
{
    /// <summary>
    /// 基础查询 SQL 构建器契约：统一生成 Count/Select/Update 与 WHERE 拼接片段。
    /// 各数据库实现负责处理自身语法差异（占位符前缀、FINAL、多值 VALUES 等）。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IQuerySqlBuilder<T>
    {
        /// <summary>
        /// 当前构建器使用的表名（构造时可覆盖为分区表）。
        /// </summary>
        string TableName { get; }

        /// <summary>
        /// 生成 COUNT 语句。
        /// </summary>
        string Count();

        /// <summary>
        /// 生成全列 SELECT 语句。
        /// </summary>
        string SelectAll();

        /// <summary>
        /// 生成 UPDATE 语句（不含 WHERE）。
        /// </summary>
        /// <param name="columns">需要更新的列</param>
        string Update(IEnumerable<string> columns);

        /// <summary>
        /// 拼接 WHERE 子句，条件为空时原样返回。
        /// </summary>
        /// <param name="sql">基础 SQL</param>
        /// <param name="whereClause">WHERE 条件体（不含 WHERE 关键字）</param>
        string ApplyWhere(string sql, string? whereClause);

        /// <summary>
        /// 生成按主键定位的条件片段。
        /// </summary>
        string WhereByKey();
    }
}
