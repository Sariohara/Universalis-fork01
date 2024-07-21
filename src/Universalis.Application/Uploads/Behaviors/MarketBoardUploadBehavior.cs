﻿using MassTransit;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Universalis.Application.Realtime.Messages;
using Universalis.Application.Uploads.Schema;
using Universalis.DbAccess.MarketBoard;
using Universalis.DbAccess.Queries.MarketBoard;
using Universalis.Entities.AccessControl;
using Universalis.Entities.MarketBoard;
using Universalis.GameData;
using Listing = Universalis.Entities.MarketBoard.Listing;
using Materia = Universalis.Entities.Materia;
using Sale = Universalis.Entities.MarketBoard.Sale;

namespace Universalis.Application.Uploads.Behaviors;

public class MarketBoardUploadBehavior : IUploadBehavior
{
    private readonly ICurrentlyShownDbAccess _currentlyShownDb;
    private readonly IHistoryDbAccess _historyDb;
    private readonly IGameDataProvider _gdp;
    private readonly IBus _bus;
    private readonly ILogger<MarketBoardUploadBehavior> _logger;

    public MarketBoardUploadBehavior(
        ICurrentlyShownDbAccess currentlyShownDb,
        IHistoryDbAccess historyDb,
        IGameDataProvider gdp,
        IBus bus,
        ILogger<MarketBoardUploadBehavior> logger)
    {
        _currentlyShownDb = currentlyShownDb;
        _historyDb = historyDb;
        _gdp = gdp;
        _bus = bus;
        _logger = logger;
    }

    public bool ShouldExecute(UploadParameters parameters)
    {
        var cond = parameters.WorldId != null;
        cond &= parameters.ItemId != null;
        cond &= parameters.Sales != null || parameters.Listings != null;

        if (cond)
        {
            var stackSize = _gdp.MarketableItemStackSizes()[parameters.ItemId.Value];

            // Validate entries; .All returns false if the list is empty, so check that first
            // TODO: Reject uploads with bad data instead of just filtering the bad data out after Dalamud fixes sales
            if (parameters.Sales?.Count > 0)
            {
                cond &= parameters.Sales.All(s => s.Quantity <= stackSize && s.PricePerUnit is <= 999_999_999);
                parameters.Sales = parameters.Sales.Where(s => s.Quantity is > 0).ToList();
            }

            if (parameters.Listings?.Count > 0)
            {
                cond &= parameters.Listings.All(s => s.Quantity <= stackSize && s.PricePerUnit is <= 999_999_999);
                parameters.Listings = parameters.Listings.Where(l => l.Quantity is > 0).ToList();
            }
        }

        return cond;
    }

    public async Task<IActionResult> Execute(ApiKey source, UploadParameters parameters,
        CancellationToken cancellationToken = default)
    {
        using var activity = Util.ActivitySource.StartActivity("MarketBoardUploadBehavior.Execute");

        // ReSharper disable PossibleInvalidOperationException
        var worldId = parameters.WorldId.Value;
        var itemId = parameters.ItemId.Value;
        // ReSharper restore PossibleInvalidOperationException

        // Add world/item to traces
        activity?.AddTag("worldId", worldId);
        activity?.AddTag("itemId", itemId);

        if (parameters.Sales != null)
        {
            if (parameters.Sales.Any(s =>
                    Util.HasHtmlTags(s.BuyerName) || Util.HasHtmlTags(s.SellerId) || Util.HasHtmlTags(s.BuyerId)))
            {
                return new BadRequestResult();
            }

            await HandleSales(parameters.Sales, itemId, worldId, parameters.UploaderId, cancellationToken);
        }

        if (parameters.Listings != null)
        {
            if (parameters.Listings.Any(l =>
                    Util.HasHtmlTags(l.ListingId) || Util.HasHtmlTags(l.RetainerName) ||
                    Util.HasHtmlTags(l.RetainerId) || Util.HasHtmlTags(l.CreatorName) || Util.HasHtmlTags(l.SellerId) ||
                    Util.HasHtmlTags(l.CreatorId)))
            {
                return new BadRequestResult();
            }

            await HandleListings(parameters.Listings, itemId, worldId, source, cancellationToken);
        }

        return null;
    }

    private async Task HandleListings(IList<Schema.Listing> uploadedListings, int itemId, int worldId,
        ApiKey source, CancellationToken cancellationToken = default)
    {
        var newListings = CleanUploadedListings(uploadedListings, itemId, worldId, source.Name);

        _ = PublishListingsToMessageBus(newListings, worldId, itemId, cancellationToken);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var document = new CurrentlyShown
        {
            WorldId = worldId,
            ItemId = itemId,
            LastUploadTimeUnixMilliseconds = now,
            UploadSource = source.Name,
            Listings = newListings,
        };
        await _currentlyShownDb.Update(document, new CurrentlyShownQuery
        {
            WorldId = worldId,
            ItemId = itemId,
        }, cancellationToken);
    }

    private async Task PublishListingsToMessageBus(IList<Listing> listings, int worldId, int itemId,
        CancellationToken cancellationToken = default)
    {
        if (_bus == null) return;

        var existingCurrentlyShown = await _currentlyShownDb.Retrieve(new CurrentlyShownQuery
        {
            WorldId = worldId,
            ItemId = itemId,
        }, cancellationToken);
        var oldListings = existingCurrentlyShown?.Listings ?? new List<Listing>();
        var addedListings = listings.Where(l => !oldListings.Contains(l)).ToList();
        var removedListings = oldListings.Where(l => !listings.Contains(l)).ToList();

        if (addedListings.Count > 0)
        {
            try
            {
                await _bus.Publish(new ListingsAdd
                {
                    WorldId = worldId,
                    ItemId = itemId,
                    Listings = addedListings
                        .Select(Util.ListingToView)
                        .ToList(),
                }, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to publish ListingsAdd event");
            }
        }

        if (removedListings.Count > 0)
        {
            try
            {
                await _bus.Publish(new ListingsRemove
                {
                    WorldId = worldId,
                    ItemId = itemId,
                    Listings = removedListings
                        .Select(Util.ListingToView)
                        .ToList(),
                }, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to publish ListingsRemove event");
            }
        }
    }

    private async Task HandleSales(IList<Schema.Sale> uploadedSales, int itemId, int worldId, string uploaderId,
        CancellationToken cancellationToken = default)
    {
        var cleanSales = CleanUploadedSales(uploadedSales, worldId, itemId, uploaderId);

        var addedSales = new List<Sale>();

        var existingHistory = await _historyDb.Retrieve(new HistoryQuery
        {
            WorldId = worldId,
            ItemId = itemId,
            Count = uploadedSales?.Count ?? 0,
        }, cancellationToken);

        if (existingHistory == null)
        {
            addedSales.AddRange(cleanSales);
            await _historyDb.Create(new History
            {
                WorldId = worldId,
                ItemId = itemId,
                LastUploadTimeUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sales = cleanSales,
            }, cancellationToken);
        }
        else
        {
            // Remove duplicates
            addedSales.AddRange(cleanSales.Where(t => !existingHistory.Sales.Contains(t)));
            await _historyDb.InsertSales(addedSales, new HistoryQuery
            {
                WorldId = worldId,
                ItemId = itemId,
            }, cancellationToken);
        }

        _ = PublishSalesToMessageBus(addedSales, itemId, worldId, cancellationToken);
    }

    private async Task PublishSalesToMessageBus(IList<Sale> sales, int itemId, int worldId,
        CancellationToken cancellationToken = default)
    {
        if (_bus != null && sales.Count > 0)
        {
            try
            {
                await _bus.Publish(new SalesAdd
                {
                    WorldId = worldId,
                    ItemId = itemId,
                    Sales = sales.Select(Util.SaleToView).ToList(),
                }, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to publish SalesAdd event");
            }
        }
    }

    private static List<Listing> CleanUploadedListings(IEnumerable<Schema.Listing> uploadedListings, int itemId,
        int worldId, string sourceName)
    {
        using var activity = Util.ActivitySource.StartActivity("MarketBoardUploadBehavior.CleanUploadedListings");

        return uploadedListings
            .Select(l =>
            {
                // Listing IDs from some uploaders are empty; this needs to be fixed
                // but this should be a decent workaround that still enables data
                // collection.
                var listingId = l.ListingId;
                if (string.IsNullOrEmpty(listingId))
                {
                    using var sha256 = SHA256.Create();
                    var hashString =
                        $"{l.CreatorId}:{l.CreatorName}:${l.RetainerName}:${l.RetainerId}:${l.SellerId}:${l.LastReviewTimeUnixSeconds}:${l.Quantity}:${l.PricePerUnit}:${string.Join(',', l.Materia)}:${itemId}:${worldId}";
                    listingId = $"dirty:{Util.Hash(sha256, hashString)}";
                }

                return new Listing
                {
                    ListingId = listingId,
                    ItemId = itemId,
                    WorldId = worldId,
                    Hq = Util.ParseUnusualBool(l.Hq),
                    OnMannequin = Util.ParseUnusualBool(l.OnMannequin),
                    Materia = l.Materia?
                        .Where(s => s.SlotId != null && s.MateriaId != null)
                        .Select(s => new Materia
                        {
                            SlotId = (int)s.SlotId!,
                            MateriaId = (int)s.MateriaId!,
                        })
                        .ToList() ?? new List<Materia>(),
                    PricePerUnit = l.PricePerUnit ?? 0,
                    Quantity = l.Quantity ?? 0,
                    DyeId = l.DyeId ?? 0,
                    CreatorId = Util.ParseUnusualId(l.CreatorId) ?? "",
                    CreatorName = l.CreatorName,
                    LastReviewTime = DateTimeOffset.FromUnixTimeSeconds(l.LastReviewTimeUnixSeconds ?? 0).UtcDateTime,
                    RetainerId = Util.ParseUnusualId(l.RetainerId) ?? "",
                    RetainerName = l.RetainerName,
                    RetainerCityId = l.RetainerCityId ?? 0,
                    SellerId = Util.ParseUnusualId(l.SellerId) ?? "",
                    Source = sourceName,
                };
            })
            .Where(l => l.PricePerUnit > 0)
            .Where(l => l.Quantity > 0)
            .OrderBy(l => l.PricePerUnit)
            .ToList();
    }

    private static List<Sale> CleanUploadedSales(IEnumerable<Schema.Sale> uploadedSales, int worldId, int itemId,
        string uploaderIdSha256)
    {
        using var activity = Util.ActivitySource.StartActivity("MarketBoardUploadBehavior.CleanUploadedSales");

        return uploadedSales
            .Where(s => s.TimestampUnixSeconds > 0)
            .Select(s => new Sale
            {
                Id = Guid.NewGuid(),
                WorldId = worldId,
                ItemId = itemId,
                Hq = Util.ParseUnusualBool(s.Hq),
                BuyerName = s.BuyerName,
                OnMannequin = Util.ParseUnusualBool(s.OnMannequin),
                PricePerUnit = s.PricePerUnit ?? 0,
                Quantity = s.Quantity ?? 0,
                SaleTime = DateTimeOffset.FromUnixTimeSeconds(s.TimestampUnixSeconds ?? 0).UtcDateTime,
                UploaderIdHash = uploaderIdSha256,
            })
            .Where(s => s.PricePerUnit > 0)
            .Where(s => s.Quantity > 0)
            .Where(s => new DateTimeOffset(s.SaleTime).ToUnixTimeSeconds() > 0)
            .OrderByDescending(s => s.SaleTime)
            .ToList();
    }
}