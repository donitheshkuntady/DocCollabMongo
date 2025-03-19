using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DocCollabMongoCore.Domain.DocumentCollab;
public record FileCollabDetails
{
    public required string RoomName { get; set; }

    public string? SfdtString { get; set; }
}

//Need to remove this
public record DocumentContent
{
#pragma warning disable IDE1006 // Naming Styles
    public int version { get; set; }

    public string? sfdt { get; set; }
#pragma warning restore IDE1006 // Naming Styles
}

//Need to uncomment this
//public record DocumentContent
//{
//	public int Version { get; set; }

//	public string? Sfdt { get; set; }
//}

public record DocCollabTempCollectionDetails
{
    public int Version { get; set; }

    public required string Operation { get; set; }
    public int ClientVersion { get; set; }

    public DateTime CreatedDate { get; set; }
}

public record DocCollabSyncVersionInfo
{
    public required string RoomName { get; set; }

    public int LastSavedVersion { get; set; }
}
