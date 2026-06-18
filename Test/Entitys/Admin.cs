using Customize_ORM.Attributes;
using System.ComponentModel.DataAnnotations.Schema;

namespace Test.Entitys
{
    [Table("admin")]
    public class AdminEntity
    {
        [Index]
        public Guid Id { get; set; }
        public Guid ParentId { get; set; }
        public int Status { get; set; }
        public string? Remark { get; set; }
        public bool IsDeleted { get; set; }
        public string? ExtraProperties { get; set; }
    }
}
