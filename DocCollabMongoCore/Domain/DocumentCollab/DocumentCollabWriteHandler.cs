﻿using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using MongoDB.Driver;
using Newtonsoft.Json;
using Syncfusion.EJ2.DocumentEditor;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DocCollabMongoCore.Domain.DocumentCollab;

public class DocumentCollabWriteHandler 
{
    protected virtual byte DocCollabSaveThreshold => ApplicationConstant.DocCollabSaveThreshold;
    private readonly RecyclableMemoryStreamManager _recyclableMemoryStreamManager;
    private readonly string _bucketName;
    private readonly ILogger<DocumentCollabWriteHandler> _logger;

    public DocumentCollabWriteHandler(IConfiguration configuration, RecyclableMemoryStreamManager recyclableMemoryStreamManager, ILogger<DocumentCollabWriteHandler> logger) : base(writeContext)
    {
        _recyclableMemoryStreamManager = recyclableMemoryStreamManager;        
        _logger = logger;
    }

    public async Task<string> ImportFileAsync(FileCollabDetails fileInfo)
    {
        var dbMasterEntity = await EntityContext.DocumentCollabMaster
                                                .Find(x => x.RoomName == fileInfo.RoomName && x.IsActive)
                                                .FirstOrDefaultAsync();
        var content = new DocumentContent();

        if (dbMasterEntity is not { } && fileInfo.SfdtString is { })
        {
            await InsertIntoDocCollabMasterAsync(fileInfo.RoomName, fileInfo.SfdtString);
            await CreateRecordForVersionInfoAsync(fileInfo.RoomName);
        }

        //Load the source document that will be loaded for the new user
        var document = await GetSourceDocumentAsync(fileInfo.RoomName);
        var collectionName = $"{ApplicationConstant.DocumentCollabTempTablePrefix}{fileInfo.RoomName}";

        var lastSyncedVersion = 0;
        var tempCollection = EntityContext.Database.GetCollection<DocCollabTempCollectionDetails>(collectionName);

        var excludeId = Builders<DocCollabTempCollectionDetails>.Projection.Exclude("_id");

        var newUpdatedActions = await tempCollection
                                    .Find(c => c.Version > lastSyncedVersion)
                                    .SortBy(c => c.Version)  // Sort in ascending order
                                    .Project<DocCollabTempCollectionDetails>(excludeId)
                                    .ToListAsync();

        if (newUpdatedActions is { } && newUpdatedActions.Count > 0)
        {
            var actions = GetOperationsQueue(newUpdatedActions);

            if (actions is { })
            {
                //Updated pending edit from database to source document.
                document.UpdateActions(actions);
            }
        }

        var json = JsonConvert.SerializeObject(document);
        content.version = lastSyncedVersion;
        content.sfdt = json;
        return JsonConvert.SerializeObject(content);
    }

    public async Task<ActionInfo?> UpdateActionAsync(ActionInfo param)
    {
        try
        {
            return await Task.Run(() => AddOperationsToCollectionAsync(param));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(message: ex.Message);
        }
    }

    public async Task<string> GetActionsFromServerAsync(ActionInfo param)
    {
        try
        {
            var collectionName = $"{ApplicationConstant.DocumentCollabTempTablePrefix}{param.RoomName}";
            var collection = EntityContext.Database.GetCollection<DocCollabTempCollectionDetails>(collectionName);

            // Fetch actions with version greater than the given version
            var filter = Builders<DocCollabTempCollectionDetails>.Filter.Gt(a => a.Version, param.Version);
            var excludeId = Builders<DocCollabTempCollectionDetails>.Projection.Exclude("_id");
            var tempEntityList = await collection.Find(filter).Project<DocCollabTempCollectionDetails>(excludeId).ToListAsync();

            if (tempEntityList.Count > 0)
            {
                var startVersion = tempEntityList[0].Version;
                // Get the lowest client version among the actions
                var lowestVersion = GetLowestClientVersion(tempEntityList);

                if (startVersion > lowestVersion)
                {
                    // Refetch actions with version >= lowest client version
                    filter = Builders<DocCollabTempCollectionDetails>.Filter.Gte(a => a.Version, lowestVersion);
                    tempEntityList = await collection.Find(filter).Project<DocCollabTempCollectionDetails>(excludeId).ToListAsync();
                }

                var actions = GetOperationsQueue(tempEntityList);
                // Transform actions if needed
                foreach (var info in actions)
                {
                    if (!info.IsTransformed)
                    {
                        CollaborativeEditingHandler.TransformOperation(info, actions);
                    }
                }

                // Filter actions with version greater than the client's version
                actions = actions.Where(a => a.Version > param.Version).ToList();

                // Serialize actions to JSON
                return JsonConvert.SerializeObject(actions);
            }

            return "{}";
        }
        catch (Exception ex)
        {
            // Log or handle the exception as needed
            return ex.ToString();
        }
    }

    private async Task<ActionInfo> AddOperationsToCollectionAsync(ActionInfo action)
    {
        try
        {
            var collectionName = $"{ApplicationConstant.DocumentCollabTempTablePrefix}{action.RoomName}";
            var collection = EntityContext.Database.GetCollection<DocCollabTempCollectionDetails>(collectionName);

            // Insert the new operation.
            var clientVersion = action.Version;
            var value = JsonConvert.SerializeObject(action);

            var updateVersion = await InsertIntoTempCollectionAsync(collection, value!, clientVersion);

            if (updateVersion - clientVersion == 1)
            {
                action.Version = updateVersion;
                UpdateCurrentActionInDB(collection, action);
            }
            else
            {
                var tempList = GetOperationsToTransform(collection, clientVersion + 1, updateVersion);
                var startVersion = tempList[0].Version;
                var lowestVersion = GetLowestClientVersion(tempList);
                if (startVersion > lowestVersion)
                {
                    tempList = GetOperationsToTransform(collection, lowestVersion, updateVersion);
                }

                var actions = GetOperationsQueue(tempList);

                foreach (var info in actions)
                {
                    if (!info.IsTransformed)
                    {
                        CollaborativeEditingHandler.TransformOperation(info, actions);
                    }
                }

                action = actions[actions.Count - 1];
                action.Version = updateVersion;
                UpdateCurrentActionInDB(collection, actions[actions.Count - 1]);
            }

            if (updateVersion % DocCollabSaveThreshold == 0)
            {
                //Saves operations from a temporary collection to the master collection
                await UpdateOperationsToMasterTableAsync(action.RoomName, collectionName, true, updateVersion);
            }

            return action;
        }
        catch (Exception ex)
        {
            _logger.LogInformation( $"Collab-UpdateAction Error in AddOperationsToCollectionAsync: for RoomName: {action.RoomName}, for User: {action.CurrentUser} - {ex}");
            throw;
        }
    }

    private static async Task<int> InsertIntoTempCollectionAsync(IMongoCollection<DocCollabTempCollectionDetails> collection, string value, int clientVersion)
    {
        try
        {
            var excludeId = Builders<DocCollabTempCollectionDetails>.Projection.Exclude("_id");
            // Find the highest existing version greater than the clientVersion
            //		var lastVersion = (await collection.AsQueryable()
            //.Select(x => (int?)x.Version)
            //.ToListAsync()) // Materialize into memory
            //.DefaultIfEmpty(0)
            //.Max();

            var maxVersionDoc = await collection.Find(FilterDefinition<DocCollabTempCollectionDetails>.Empty)
                                .SortByDescending(x => x.Version)
                                .Limit(1)
                                .Project<DocCollabTempCollectionDetails>(excludeId)
                                .FirstOrDefaultAsync();

            var lastVersion = maxVersionDoc?.Version ?? 0; // Default to 0 if no document exists

            //.Project<DocCollabTempCollectionDetails>(excludeId)
            //.FirstOrDefaultAsync();

            var updateVersion = lastVersion is { } ? lastVersion + 1 : 1;

            var newTempRecord = new DocCollabTempCollectionDetails()
            {
                Version = updateVersion,
                Operation = value,
                ClientVersion = clientVersion,
                CreatedDate = DateTime.UtcNow
            };
            await collection.InsertOneAsync(newTempRecord);

            return updateVersion;
        }
        catch (Exception ex)
        {
#pragma warning disable CA2200 // Rethrow to preserve stack details
#pragma warning disable S3445 // Exceptions should not be explicitly rethrown
            throw ex;
#pragma warning restore S3445 // Exceptions should not be explicitly rethrown
#pragma warning restore CA2200 // Rethrow to preserve stack details
        }
    }

    //private async Task<ActionInfo> AddOperationsToCollectionAsyncOld(ActionInfo action)
    //{
    //	var collectionName = $"{ApplicationConstant.DocumentCollabTempTablePrefix}{action.RoomName}";
    //	var collection = EntityContext.Database.GetCollection<ActionInfo>(collectionName);

    //	// Insert the new operation.
    //	var clientVersion = action.Version;

    //	// Check if the version already exists - To avoid version conflicts when multiple people are editing the document
    //	var existingVersion = await collection.Find(a => a.Version == clientVersion).FirstOrDefaultAsync();
    //	if (existingVersion is { })
    //	{
    //		// Find the highest existing version greater than the clientVersion
    //		var lastVersion = await collection
    //			.Find(a => a.Version > clientVersion)
    //			.SortByDescending(a => a.Version)
    //			.FirstOrDefaultAsync();

    //		// If a version greater than clientVersion exists, increment it by 1
    //		if (lastVersion is not null)
    //		{
    //			clientVersion = lastVersion.Version + 1;
    //		}
    //		else
    //		{
    //			clientVersion += 1; // Default to incrementing by 1 if no greater version exists
    //		}
    //	}

    //	action.Version = clientVersion + 1; // Assuming auto-increment logic.
    //	await collection.InsertOneAsync(action);

    //	if (clientVersion == 0)
    //	{
    //		await CreateRecordForVersionInfoAsync(action.RoomName);
    //	}

    //	if (action.Version - clientVersion > 0)
    //	{
    //		//Updates a specific action in the database, marking it as transformed
    //		UpdateCurrentActionInDB(collection, action);
    //	}
    //	else
    //	{
    //		//Transforms and applies actions within a version range
    //		TransformAndApplyActions(collection, clientVersion, action.Version, ref action);
    //	}

    //	if (clientVersion == 0 || action.Version % DocCollabSaveThreshold == 0)
    //	{
    //		//Saves operations from a temporary collection to the master collection
    //		_ = UpdateOperationsToMasterTableAsync(action.RoomName, collectionName, true);
    //	}

    //	return action;
    //}

    private async Task<WordDocument> GetSourceDocumentAsync(string roomName)
    {
        try
        {
            var dbMasterCollection = await EntityContext.DocumentCollabMaster
                                                .Find(x => x.RoomName == roomName && x.IsActive)
                                                .FirstOrDefaultAsync();

            var document = new WordDocument();
            if (dbMasterCollection is { } && dbMasterCollection.StorageIdentifier is { })
            {
                document = await DownloadDocCollabDocAsync(dbMasterCollection.RoomName, dbMasterCollection.StorageIdentifier);
            }

            return document;
        }
        catch (Exception ex)
        {
#pragma warning disable CA2200 // Rethrow to preserve stack details
#pragma warning disable S3445 // Exceptions should not be explicitly rethrown
            throw ex;
#pragma warning restore S3445 // Exceptions should not be explicitly rethrown
#pragma warning restore CA2200 // Rethrow to preserve stack details
        }
    }

    private async Task InsertIntoDocCollabMasterAsync(string roomName, string sfdtString)
    {
        try
        {
            var isValidSfdt = IsValidWordSfdt(sfdtString);

            if (!isValidSfdt)
            {
                var validationResult = new ValidationResult($"{sfdtString} is invalid");
                throw new InvalidDataException(validationResult.ErrorMessage);
            }

            // Upload the sfdt content to s3
            var storageIdentifier = await UploadDocCollabTextAsync(roomName, sfdtString);

            // Map ActionInfo to DocumentCollabMaster.
            var masterDocument = new DocumentCollabMaster
            {
                RoomName = roomName,
                StorageIdentifier = storageIdentifier,
                Version = 0,
                IsActive = true,
                CreatedByUserId = Principal.UserGuid.ToString(),
                CreatedDate = DateTimeOffset.UtcNow,
                LastModifiedByUserId = Principal.UserGuid.ToString(),
                LastModifiedDate = DateTimeOffset.UtcNow,
            };

            await EntityContext.DocumentCollabMaster.InsertOneAsync(masterDocument);
        }
        catch (Exception ex)
        {
#pragma warning disable CA2200 // Rethrow to preserve stack details
#pragma warning disable S3445 // Exceptions should not be explicitly rethrown
            throw ex;
#pragma warning restore S3445 // Exceptions should not be explicitly rethrown
#pragma warning restore CA2200 // Rethrow to preserve stack details
        }
    }

    private Task CreateRecordForVersionInfoAsync(string roomName)
    {
        try
        {
            var collectionName = $"{ApplicationConstant.DocumentCollabTempTableVersionInfo}";
            var collection = EntityContext.Database.GetCollection<DocCollabSyncVersionInfo>(collectionName);

            var filter = Builders<DocCollabSyncVersionInfo>.Filter.Eq("RoomName", roomName);

            var update = Builders<DocCollabSyncVersionInfo>.Update
                .Set("RoomName", roomName)
                .Set("LastSavedVersion", 0);

            _ = collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
#pragma warning disable CA2200 // Rethrow to preserve stack details
#pragma warning disable S3445 // Exceptions should not be explicitly rethrown
            throw ex;
#pragma warning restore S3445 // Exceptions should not be explicitly rethrown
#pragma warning restore CA2200 // Rethrow to preserve stack details
        }
    }

    private async Task<int> GetLastedSyncedVersionAsync(string roomName)
    {
        var collectionName = $"{ApplicationConstant.DocumentCollabTempTableVersionInfo}";
        var collection = EntityContext.Database.GetCollection<DocCollabSyncVersionInfo>(collectionName);

        var filter = Builders<DocCollabSyncVersionInfo>.Filter.Eq("RoomName", roomName);
        var excludeId = Builders<DocCollabSyncVersionInfo>.Projection.Exclude("_id");

        var document = await collection.Find(filter)
            .Project<DocCollabSyncVersionInfo>(excludeId).FirstOrDefaultAsync();

        return document.LastSavedVersion;
    }

    private Task UpdateModifiedVersionAsync(string roomName, int lastSavedVersion)
    {
        var collectionName = $"{ApplicationConstant.DocumentCollabTempTableVersionInfo}";
        var collection = EntityContext.Database.GetCollection<DocCollabSyncVersionInfo>(collectionName);

        var filter = Builders<DocCollabSyncVersionInfo>.Filter.Eq("RoomName", roomName);
        var update = Builders<DocCollabSyncVersionInfo>.Update.Set("LastSavedVersion", lastSavedVersion);

        _ = collection.UpdateOneAsync(filter, update);
        return Task.CompletedTask;
    }

    private void DeleteVersionInfo(string roomName)
    {
        var collectionName = $"{ApplicationConstant.DocumentCollabTempTableVersionInfo}";
        var collection = EntityContext.Database.GetCollection<DocCollabSyncVersionInfo>(collectionName);

        var filter = Builders<DocCollabSyncVersionInfo>.Filter.Eq("RoomName", roomName);

        _ = collection.DeleteOneAsync(filter);
    }

    private static void UpdateCurrentActionInDB(IMongoCollection<DocCollabTempCollectionDetails> collection, ActionInfo action)
    {
        action.IsTransformed = true;
        var filter = Builders<DocCollabTempCollectionDetails>.Filter.Eq(a => a.Version, action.Version);
        var update = Builders<DocCollabTempCollectionDetails>.Update
            .Set(a => a.Operation, JsonConvert.SerializeObject(action));
        collection.UpdateOne(filter, update);
    }

    private static List<DocCollabTempCollectionDetails> GetOperationsToTransform(IMongoCollection<DocCollabTempCollectionDetails> collection, int clientVersion, int currentVersion)
    {
        var filter = Builders<DocCollabTempCollectionDetails>.Filter.Gte(a => a.Version, clientVersion) &
                     Builders<DocCollabTempCollectionDetails>.Filter.Lte(a => a.Version, currentVersion);
        var excludeId = Builders<DocCollabTempCollectionDetails>.Projection.Exclude("_id");

        //return collection.Find(filter).Project<DocCollabTempCollectionDetails>(excludeId).ToList();
        return collection
                .Find(filter)
                .SortBy(a => a.Version)
                .Project<DocCollabTempCollectionDetails>(excludeId)
                .ToList();
    }

    private static int GetLowestClientVersion(List<DocCollabTempCollectionDetails> tableList)
    {
        var clientVersion = tableList[0].ClientVersion;
        foreach (var row in tableList)
        {
            //TODO: Need to optimise version calculation for only untransformed operations
            var version = row.ClientVersion;
            if (version < clientVersion)
            {
                clientVersion = version;
            }
        }

        return clientVersion;
    }

    //private static void TransformAndApplyActions(IMongoCollection<ActionInfo> collection, int clientVersion, int currentVersion, ref ActionInfo action)
    //{
    //	var filter = Builders<ActionInfo>.Filter.Gte(a => a.Version, clientVersion) &
    //				 Builders<ActionInfo>.Filter.Lte(a => a.Version, currentVersion);
    //	var excludeId = Builders<ActionInfo>.Projection.Exclude("_id");

    //	var actionsQueue = collection.Find(filter).Project<ActionInfo>(excludeId).ToList();

    //	foreach (var info in actionsQueue)
    //	{
    //		if (!info.IsTransformed)
    //		{
    //			CollaborativeEditingHandler.TransformOperation(info, actionsQueue);
    //		}
    //	}

    //	if (actionsQueue.Count > 0) // Ensure the list is not empty
    //	{
    //		action = actionsQueue[^1]; // Use indexing to get the last element (C# index from end operator)
    //	}

    //	UpdateCurrentActionInDB(collection, action);
    //}

    public async Task UpdateOperationsToMasterTableAsync(string roomName, string tempCollectionName, bool partialSave, int endVersion)
    {
        try
        {
            var tempCollection = EntityContext.Database.GetCollection<DocCollabTempCollectionDetails>(tempCollectionName);

            var excludeId = Builders<DocCollabTempCollectionDetails>.Projection.Exclude("_id");

            var lastSyncedVersion = await GetLastedSyncedVersionAsync(roomName);

            var newUpdatedActions = partialSave ?
                await tempCollection
                .Find(c => c.Version > lastSyncedVersion && c.Version <= endVersion)
                .Project<DocCollabTempCollectionDetails>(excludeId).ToListAsync() :
                    await tempCollection
                    .Find(c => c.Version > lastSyncedVersion)
                    .Project<DocCollabTempCollectionDetails>(excludeId).ToListAsync();

            if (newUpdatedActions is { })
            {
                var actions = GetOperationsQueue(newUpdatedActions);
                foreach (var info in actions)
                {
                    // Process transformations.
                    if (!info.IsTransformed)
                    {
                        CollaborativeEditingHandler.TransformOperation(info, actions);
                    }
                }

                // Fetch the latest operation for the RoomName based on the highest version number. - TODO MAx
                var latestOperation = newUpdatedActions
                    .OrderByDescending(a => a.Version)
                    .First();

                var dbMasterCollection = await EntityContext.DocumentCollabMaster
                                                .Find(x => x.RoomName == roomName && x.IsActive)
                                                .FirstOrDefaultAsync();

                // Start with a new document if no previous document exists
                //var document = new WordDocument();

                using var document = new Syncfusion.DocIO.DLS.WordDocument();
                document.EnsureMinimal();
                var ej2Document = WordDocument.Load(document);

                if (dbMasterCollection is { } && dbMasterCollection.StorageIdentifier is { })
                {
                    ej2Document = await DownloadDocCollabDocAsync(dbMasterCollection.RoomName, dbMasterCollection.StorageIdentifier);
                }

                // Apply the latest operation's changes to the document
                var handler = new CollaborativeEditingHandler(ej2Document);
                _logger.LogMessage(logLevel: LogLevel.Information, message: $"Collab-UpdateAction: Appending actions started at Version: {endVersion} and LastSyncedVersion: {lastSyncedVersion}");
                foreach (var action in actions)
                {
                    handler.UpdateAction(action);
                }

                _logger.LogMessage(logLevel: LogLevel.Information, message: $"Collab-UpdateAction: SyncFusion UpdateAction was completed successfully");

                //// Serialize the updated document to S3
                //using var memoryStream = _recyclableMemoryStreamManager.GetStream();
                //ej2Document.Save(memoryStream, Syncfusion.DocIO.FormatType.Docx);
                //memoryStream.Position = 0;

                //// Convert the memory stream to SFDT content
                //using var reader = new StreamReader(memoryStream);
                //var sfdtContent = await reader.ReadToEndAsync();

                // Upload the sfdt content to s3
                var sfdtContent = JsonConvert.SerializeObject(handler.Document);

                var storageIdentifier = await UploadDocCollabTextAsync(roomName, sfdtContent);

                // Map ActionInfo to DocumentCollabMaster.
                var masterDocument = new DocumentCollabMaster
                {
                    RoomName = roomName,
                    StorageIdentifier = storageIdentifier,
                    Version = latestOperation.Version,
                    IsActive = partialSave,
                    CreatedByUserId = dbMasterCollection is { } ? dbMasterCollection.CreatedByUserId : Principal.UserGuid.ToString(),
                    CreatedDate = dbMasterCollection is { } ? dbMasterCollection.CreatedDate : DateTimeOffset.UtcNow,
                    LastModifiedByUserId = Principal.UserGuid.ToString(),
                    LastModifiedDate = DateTimeOffset.UtcNow,
                };

                // Upsert into master collection.
                var masterFilter = dbMasterCollection is { } ? Builders<DocumentCollabMaster>.Filter.Eq(m => m.Id, dbMasterCollection.Id) : null;

                var masterUpdate = Builders<DocumentCollabMaster>.Update
                    .Set(m => m.RoomName, masterDocument.RoomName)
                    .Set(m => m.StorageIdentifier, masterDocument.StorageIdentifier)
                    .Set(m => m.Version, masterDocument.Version)
                    .Set(m => m.IsActive, masterDocument.IsActive)
                    .Set(m => m.CreatedByUserId, masterDocument.CreatedByUserId)
                    .Set(m => m.CreatedDate, masterDocument.CreatedDate)
                    .Set(m => m.LastModifiedByUserId, masterDocument.LastModifiedByUserId)
                    .Set(m => m.LastModifiedDate, masterDocument.LastModifiedDate);

                await EntityContext.DocumentCollabMaster.UpdateOneAsync(masterFilter, masterUpdate, new UpdateOptions { IsUpsert = true });

                if (!partialSave)
                {
                    endVersion = actions[actions.Count - 1].Version;
                }

                // Delete the older version of the sfdt content uploaded to s3 for this roomName
                if (dbMasterCollection is { })
                {
                    //TODO: check this logic wrt to saving the source doc
                    DeleteS3DocCollabFile(roomName, dbMasterCollection.StorageIdentifier!);
                }
            }

            if (!partialSave)
            {
                await PublishDocCollabMasterEventAsync(roomName);
                DropTemporaryCollection(tempCollectionName);
                DeleteVersionInfo(roomName);
            }
            else
            {
                await UpdateModifiedVersionAsync(roomName, endVersion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogMessage(logLevel: LogLevel.Error, message: $"Collab-UpdateAction Error in UpdateOperationsToMasterTableAsync: for RoomName: {roomName}, for Version: {endVersion} - {ex}");
            throw;
        }
    }

    public async Task<WordDocument> DownloadDocCollabDocAsync(string roomName, string storageIdentifier)
    {
        //TODO: Convert the sdft string to a document and then Load - also apply changes to Upload
        var fileFullPath = ToPath(roomName, storageIdentifier);
        var (fileResponse, readStreamAsync) = await _storageHandler.ReadAsync(_bucketName, fileFullPath);

        using var stream = _recyclableMemoryStreamManager.GetStream();
        await readStreamAsync(stream);

        stream.Position = 0;

        // Load and return the WordDocument
        var document = WordDocument.Load(stream, FormatType.Docx);

        // Dispose the response object
        fileResponse.Dispose();

        return document;
    }

    public async Task PublishDocCollabMasterEventAsync(string roomName)
    {
        var eventMessageId = GetNewGuid();

        var dbMasterCollection = await EntityContext.DocumentCollabMaster
                                        .Find(x => x.RoomName == roomName)
                                        .SortByDescending(x => x.CreatedDate)
                                        .FirstOrDefaultAsync();

        //if (dbMasterCollection is { })
        //{
        //    var docCollabMasterToPublish = dbMasterCollection.ToDocCollabMasterPublishEvent(eventMessageId);
        //    var eventData = VersionedValue.FromInsert(docCollabMasterToPublish);
        //    await _messageLogWriteHandler.WriteAsync(new EventMessageLogFacade { Id = eventMessageId.ToString(), TimeSent = NowOffsetUtc, Content = JsonSerializer.Serialize(eventData), Domain = EventDomain.DocCollab, IsGlobal = true });

        //    await EventService.PublishAsync(new EventPublishDetails(eventData, EventDomain.DocCollab, EventAction.Insert, Principal) { IsGlobal = true });
        //}
    }

    private static List<ActionInfo> GetOperationsQueue(List<DocCollabTempCollectionDetails> tempCollectionData)
    {
        var actions = new List<ActionInfo>();
        foreach (var row in tempCollectionData)
        {
            if (row.Operation is { })
            {
                var action = JsonConvert.DeserializeObject<ActionInfo>(row.Operation.ToString());
                if (action is { })
                {
                    action.Version = row.Version;
                    action.ClientVersion = row.ClientVersion;
                    actions.Add(action);
                }
            }
        }
        //sort the action list
        actions.Sort((x, y) => x.Version.CompareTo(y.Version));

        return actions;
    }

    public void DropTemporaryCollection(string collectionName) => EntityContext.Database.DropCollection(collectionName);

    private async Task<string> UploadDocCollabTextAsync(string roomName, string sfdtContent)
    {
        //TODO:Do not convert the sfdt to document and upload - keep it a sfdt.
        var document = WordDocument.Save(sfdtContent);
        var memoryStream = _recyclableMemoryStreamManager.GetStream();

        document.Save(memoryStream, Syncfusion.DocIO.FormatType.Docx);
        memoryStream.Position = 0;

        var storageIdentifier = Guid.NewGuid().ToString();
        var fileFullPath = ToPath(roomName, storageIdentifier);
        await _storageHandler.WriteAsync(_bucketName, fileFullPath, memoryStream);
        return storageIdentifier;
    }
    private void DeleteS3DocCollabFile(string roomName, string storageIdentifier)
    {
        var fileFullPath = ToPath(roomName, storageIdentifier);
        _ = _storageHandler.DeleteAsync(_bucketName, fileFullPath);
    }

    public static string ToPath(string room, string key) => $"DocCollabMaster/{room}/{key}";

    public static bool IsValidWordSfdt(string sfdtContent)
    {
        try
        {
            WordDocument.Save(sfdtContent);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}