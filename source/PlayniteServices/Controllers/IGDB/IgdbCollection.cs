﻿using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Playnite.SDK;
using System.Net.Http;

namespace PlayniteServices.Controllers.IGDB;

public interface IIgdbCollection
{
    string EndpointPath { get; }
    Task CloneCollection();
    Task ConfigureWebhooks(List<Webhook> webhooksStatus);
}

public class IgdbCollection<T> : IIgdbCollection  where T : class, IIgdbItem
{
    private static readonly ILogger logger = LogManager.GetLogger();
    private readonly Database database;
    private readonly string dbCollectionName;
    internal IMongoCollection<T> collection;
    internal readonly IgdbApi igdb;

    public string EndpointPath { get; }

    public IgdbCollection(string endpointPath, IgdbApi igdb, Database database)
    {
        this.igdb = igdb;
        this.database = database;
        EndpointPath = endpointPath;
        dbCollectionName = $"IGDB_col_{endpointPath}";
        collection = database.MongoDb.GetCollection<T>(dbCollectionName);
        CreateIndexes();
    }

    public virtual void CreateIndexes()
    {
    }

    public virtual void DropCollection()
    {
        database.MongoDb.DropCollection(dbCollectionName);
        collection = database.MongoDb.GetCollection<T>(dbCollectionName);
        CreateIndexes();
    }

    public async Task CloneCollection()
    {
        DropCollection();
        var colCount = await GetCollectionCount();
        logger.Debug($"{EndpointPath} clone start, {colCount} items, {DateTime.Now:HH:mm:ss}");

        var i = 0;
        while (true)
        {
            var query = $"fields *; limit 500; offset {i};";
            var stringData = await igdb.SendStringRequest(EndpointPath, query);
            var items = DataSerialization.FromJson<List<T>>(stringData);
            if (!items.HasItems())
            {
                break;
            }

            await Add(items);

            if (items.Count < 500)
            {
                break;
            }

            i += 500;
            if (i % 5000 == 0)
            {
                logger.Debug($"{EndpointPath} clone progress {i}");
            }
        }

        logger.Debug($"DB clone {EndpointPath} end {DateTime.Now:HH:mm:ss}");
    }

    public async Task ConfigureWebhooks(List<Webhook> webhooksStatus)
    {
        if (igdb.Settings.Settings.IGDB?.WebHookRootAddress.IsNullOrEmpty() == true)
        {
            throw new Exception($"Can't register IGDB webhook, WebHookRootAddress is not configured.");
        }

        if (igdb.Settings.Settings.IGDB?.WebHookSecret.IsNullOrEmpty() == true)
        {
            throw new Exception($"Can't register IGDB webhook, WebHookSecret is not configured.");
        }

        await RegisterWebhook("create", webhooksStatus);
        await RegisterWebhook("delete", webhooksStatus);
        await RegisterWebhook("update", webhooksStatus);
    }

    private async Task RegisterWebhook(string method, List<Webhook> webhooksStatus)
    {
        var webhookUrl = igdb.Settings.Settings.IGDB!.WebHookRootAddress!.UriCombine(EndpointPath, method);
        if (webhooksStatus?.Any(a => a.url == webhookUrl) != true)
        {
            logger.Error($"IGDB {EndpointPath} {method} webhook is NOT active.");

            var registeredStr = await igdb.SendStringRequest(
                $"{EndpointPath}/webhooks",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "method", method },
                    { "secret", igdb.Settings.Settings.IGDB!.WebHookSecret! },
                    { "url", webhookUrl }
                }),
                HttpMethod.Post,
                true);
            var registeredHooks = DataSerialization.FromJson<List<Webhook>>(registeredStr);
            if (!registeredHooks.HasItems() || !registeredHooks[0].active)
            {
                logger.Error($"Failed to register {EndpointPath} {method} IGDB webhook.");
            }
            else
            {
                logger.Info($"Registered new {EndpointPath} {method} IGDB webhook.");
            }

            logger.Debug(registeredStr);
        }
        else
        {
            logger.Info($"IGDB {EndpointPath} {method} webhook is active.");
        }
    }

    private async Task<long> GetCollectionCount()
    {
        var stringResult = await igdb.SendStringRequest(EndpointPath + "/count", null, HttpMethod.Post, true);
        var response = DataSerialization.FromJson<Dictionary<string, long>>(stringResult);
        if (response?.TryGetValue("count", out var count) == null)
        {
            logger.Error(stringResult);
            throw new Exception($"Failed to get item count from {EndpointPath} colletion");
        }
        else
        {
            return count;
        }
    }

    public async Task<T?> GetItem(ulong itemId)
    {
        if (itemId == 0)
        {
            return null;
        }

        var item = await collection.Find(a => a.id == itemId).FirstOrDefaultAsync();
        return item;
        //if (item != null)
        //{
        //    return item;
        //}

        //var stringResult = await igdb.SendStringRequest(EndpointPath, $"fields *; where id = {itemId};");
        //var items = DataSerialization.FromJson<List<T>>(stringResult);
        //if (items.HasItems())
        //{
        //    await Add(items[0]);
        //    return items[0];
        //}

        //return null;
    }

    public async Task<List<T>?> GetItem(List<ulong>? itemIds)
    {
        if (!itemIds.HasItems())
        {
            return null;
        }

        var filter = Builders<T>.Filter.In(nameof(IIgdbItem.id), itemIds);
        var items = await collection.Find(filter).ToListAsync();
        //if (items.Count == itemIds.Count)
        //{
        //    return items;
        //}

        //var idsToGet = ListExtensions.GetDistinctItemsP(itemIds, items.Select(a => a.id));
        //var stringResult = await igdb.SendStringRequest(EndpointPath, $"fields *; where id = ({string.Join(',', idsToGet)}); limit 500;");
        //var newItems = DataSerialization.FromJson<List<T>>(stringResult);
        //if (newItems.HasItems())
        //{
        //    await Add(newItems);
        //    items.AddRange(newItems);
        //}

        return items;
    }

    public async Task Add(List<T> items)
    {
        var bulkOps = new List<WriteModel<T>>(items.Count);
        foreach (var item in items)
        {
            var upsertOne = new ReplaceOneModel<T>(Builders<T>.Filter.Where(a => a.id == item.id), item)
            {
                IsUpsert = true
            };

            bulkOps.Add(upsertOne);
        }

        await collection.BulkWriteAsync(bulkOps);
    }

    public async Task Add(T item)
    {
        await collection.ReplaceOneAsync(a => a.id == item.id, item, Database.ItemUpsertOptions);
    }

    public async Task Delete(T item)
    {
        await collection.DeleteOneAsync(a => a.id == item.id);
    }
}

public partial class AlternativeNameCollection : IgdbCollection<AlternativeName>
{
    public override void CreateIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<AlternativeName>(Builders<AlternativeName>.IndexKeys.Text(x => x.name)));
        collection.Indexes.CreateOne(new CreateIndexModel<AlternativeName>(Builders<AlternativeName>.IndexKeys.Ascending(x => x.game)));
    }
}

public partial class ExternalGameCollection : IgdbCollection<ExternalGame>
{
    public override void CreateIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<ExternalGame>(Builders<ExternalGame>.IndexKeys.Ascending(x => x.uid).Ascending(x => x.category)));
    }
}

public partial class GameCollection : IgdbCollection<Game>
{
    public override void CreateIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<Game>(Builders<Game>.IndexKeys.Text(x => x.name)));
        collection.Indexes.CreateOne(new CreateIndexModel<Game>(Builders<Game>.IndexKeys.Ascending(x => x.category)));
    }
}

public partial class GameLocalizationCollection : IgdbCollection<GameLocalization>
{
    public override void CreateIndexes()
    {
        collection.Indexes.CreateOne(new CreateIndexModel<GameLocalization>(Builders<GameLocalization>.IndexKeys.Text(x => x.name)));
        collection.Indexes.CreateOne(new CreateIndexModel<GameLocalization>(Builders<GameLocalization>.IndexKeys.Ascending(x => x.game)));
    }
}