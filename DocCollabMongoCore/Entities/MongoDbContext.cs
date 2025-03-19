using DocCollabMongoCore.Entities;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

public class MongoDbContext
{
    public IMongoDatabase Database;

    public MongoDbContext(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"];
        var databaseName = configuration["MongoDB:DatabaseName"];

        var client = new MongoClient(connectionString);
        Database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<DocumentCollabMaster> DocumentCollabMaster => Database.GetCollection<DocumentCollabMaster>("DocumentCollabMaster");
}
