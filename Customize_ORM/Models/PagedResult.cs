using System;
using System.Collections.Generic;
using System.Text;

namespace Customize_ORM.Models
{
    /// <summary>
    /// 分页查询结果类
    /// </summary>
    public class PagedResult<T>(List<T> items, int pageIndex, int pageSize, long totalCount)
    {
        public List<T> Items { get; } = items;
        public int PageIndex { get; } = pageIndex;
        public int PageSize { get; } = pageSize;
        public long TotalCount { get; } = totalCount;
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}
