namespace Customize_ORM.Interfaces.Sql
{
    /// <summary>
    /// 批量更新 SQL 构建契约：负责 staging 表/临时表结构、更新语句以及重试场景的主键回读片段。
    /// 各数据库的差异（UNLOGGED、多值 INSERT、RETURNING 等）由具体实现承担。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IBulkUpdateSqlBuilder<T>
    {
        /// <summary>
        /// 生成列定义片段（列名 + 目标库类型）。
        /// </summary>
        /// <param name="columns">需要定义的列</param>
        string BuildColumnDefinitions(IEnumerable<string> columns);

        /// <summary>
        /// 生成 staging 表建表语句。
        /// </summary>
        /// <param name="stagingTableName">staging 表名</param>
        string BuildCreateStagingTableSql(string stagingTableName);

        /// <summary>
        /// 生成临时表建表语句。
        /// </summary>
        /// <param name="tableName">临时表名</param>
        /// <param name="columns">临时表列</param>
        string BuildCreateTemporaryTableSql(string tableName, IEnumerable<string> columns);

        /// <summary>
        /// 生成从源表更新目标表的 UPDATE ... FROM 语句。
        /// </summary>
        /// <param name="targetTableName">目标表名</param>
        /// <param name="sourceTableName">源表名</param>
        /// <param name="sourceAlias">源表别名</param>
        /// <param name="updateColumns">更新列</param>
        /// <param name="additionalWhereClause">额外的过滤条件（不含 WHERE 关键字）</param>
        /// <param name="sourcePredicate">源表过滤谓词</param>
        string BuildUpdateFromSourceSql(
            string targetTableName,
            string sourceTableName,
            string sourceAlias,
            IEnumerable<string> updateColumns,
            string? additionalWhereClause = null,
            string? sourcePredicate = null);

        /// <summary>
        /// 生成临时表策略的 UPDATE 语句（源表无别名，直接以表名引用）。
        /// </summary>
        /// <param name="targetTableName">目标表名</param>
        /// <param name="sourceTableName">临时表名</param>
        /// <param name="updateColumns">更新列</param>
        /// <param name="additionalWhereClause">额外的过滤条件（不含 WHERE 关键字）</param>
        string BuildLegacyTemporaryUpdateSql(
            string targetTableName,
            string sourceTableName,
            IEnumerable<string> updateColumns,
            string? additionalWhereClause = null);
    }
}
