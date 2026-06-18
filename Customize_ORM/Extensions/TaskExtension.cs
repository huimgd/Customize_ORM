using System;
using System.Collections.Generic;
using System.Text;

namespace Customize_ORM.Extensions
{
    public static class TaskExtension
    {
        /// <summary>
        /// 忽略 Token 取消异常（OperationCanceledException）
        /// </summary>
        public static async Task IgnoreCancel(this Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                //ignored
            }
        }
    }
}
