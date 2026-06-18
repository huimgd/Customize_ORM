namespace Customize_ORM.Attributes
{
    [AttributeUsage(AttributeTargets.Property)]
    public class DateTimeDefaultValueAttribute : DefaultValueAttribute
    {
        public DateTimeDefaultValueAttribute(string dateTimeExpression, bool onlyUpdate = true)
            : base(dateTimeExpression, onlyUpdate)
        {
        }

        public DateTimeOffset? GetValue()
        {
            if (Value is string strValue)
            {
                return strValue.ToLower() switch
                {
                    "now" => DateTime.Now,
                    "utcnow" => DateTime.UtcNow,
                    "today" => DateTime.Today,
                    _ => DateTime.TryParse(strValue, out var dt) ? dt : DateTime.MinValue
                };
            }
            return base.GetValue<DateTime>();
        }
    }
}
