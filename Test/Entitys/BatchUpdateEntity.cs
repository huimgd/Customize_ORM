using Customize_ORM.Attributes;
using System.ComponentModel.DataAnnotations.Schema;

namespace Test.Entitys
{
    [Table("batch_update_entity")]
    public class BatchUpdateEntity
    {
        [Index]
        public Guid Id { get; set; }

        [PartitionKey]
        public DateTime CreatedAt { get; set; }

        public ulong Counter { get; set; }

        public string[]? Tags { get; set; }

        public int Status { get; set; }
    }
}
