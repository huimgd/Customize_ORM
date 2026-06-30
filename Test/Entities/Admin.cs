using Customize_ORM.Attributes;
using System.ComponentModel.DataAnnotations.Schema;

namespace Test.Entities
{
    [Table("admin")]
    public class AdminEntity
    {
        [Index]
        public Guid Id { get; set; }
        public Guid ParentId { get; set; }
        public int Status { get; set; }
        public int? NullableInt { get; set; }
        public string? Name { get; set; }
        public string? Remark { get; set; }
        public bool IsDeleted { get; set; }
        public string? ExtraProperties { get; set; }
    }
}
