namespace Customize_ORM.Attributes
{
    /// <summary>
    /// 忽略特性
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Method)]
    public class IgnoreAttribute : Attribute
    {
    }
}
