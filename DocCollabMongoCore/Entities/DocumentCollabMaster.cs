using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DocCollabMongoCore.Entities;
public class DocumentCollabMaster
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfDefault]
    public string Id { get; set; }
    public string RoomName { get; set; }
    public string? StorageIdentifier { get; set; }
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public string? CreatedByUserId { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public string? LastModifiedByUserId { get; set; }
}
