namespace Customize_ORM.Interfaces.Sql
{
    /// <summary>
    /// 单条/批量 INSERT 语句构建契约。
    /// PostgreSQL 走 COPY 二进制导入，虚谷走多值 VALUES 语句，差异由各自实现承担。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IBulkInsertSqlBuilder<T>
    {
        /// <summary>
        /// 生成单条插入语句。
        /// </summary>
        /// <param name="columns">插入列</param>
        string BuildSingleInsertSql(IEnumerable<string> columns);

        /// <summary>
        /// 生成一次承载多行的批量插入语句。
        /// </summary>
        /// <param name="columns">插入列</param>
        /// <param name="rowCount">本语句承载的行数</param>
        string BuildNamedInsertSql(IEnumerable<string> columns, int rowCount);

        /// <summary>
        /// 生成 PostgreSQL 风格的 COPY 二进制导入语句；不支持 COPY 的实现抛 <see cref="NotSupportedException"/>。
        /// </summary>
        /// <param name="columns">导入列</param>
        string CopyBinaryImport(IEnumerable<string> columns);
    }
}
