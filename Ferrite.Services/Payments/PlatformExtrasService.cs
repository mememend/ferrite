// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.dto;
using Ferrite.TL.baseLayer.help;
using Ferrite.TL.baseLayer.payments;
using Ferrite.Utils;

namespace Ferrite.Services.Payments;

/// <summary>
/// Local stand-ins for the payments/help surfaces that the official clients use
/// for Premium and collectible (NFT) gifts. Without them the client shows
/// METHOD_DISABLED and hides the whole section.
/// </summary>
public sealed class PlatformExtrasService
{
    private static readonly (int Months, long Amount)[] PremiumOptions =
    [
        (1, 500),
        (3, 1500),
        (6, 2800),
        (12, 5000),
    ];

    private readonly ILogger _log;

    public PlatformExtrasService(ILogger log)
    {
        _log = log;
    }

    [TLFunction(Constructors.baseLayer_GetPremiumPromo)]
    public ValueTask<TLBytes> GetPremiumPromo(long authKeyId, TLBytes q)
    {
        Vector options = new();
        foreach ((int months, long amount) in PremiumOptions)
        {
            using var option = PremiumSubscriptionOption.Builder()
                .Months(months)
                .Currency("XTR"u8)
                .Amount(amount)
                .BotUrl(""u8)
                .Build();
            options.AppendTLObject(option.ToReadOnlySpan());
        }

        var promo = PremiumPromo.Builder()
            .StatusText("Premium is enabled on this server."u8)
            .StatusEntities(new Vector())
            .VideoSections(new VectorOfString())
            .Videos(new Vector())
            .PeriodOptions(options)
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(promo.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetPremiumGiftCodeOptions)]
    public ValueTask<TLBytes> GetPremiumGiftCodeOptions(long authKeyId, TLBytes q)
    {
        Vector options = new();
        foreach ((int months, long amount) in PremiumOptions)
        {
            using var option = PremiumGiftCodeOption.Builder()
                .Users(1)
                .Months(months)
                .Currency("XTR"u8)
                .Amount(amount)
                .Build();
            options.AppendTLObject(option.ToReadOnlySpan());
        }
        return ValueTask.FromResult(VectorBytes(options));
    }

    [TLFunction(Constructors.baseLayer_GetSavedInfo)]
    public ValueTask<TLBytes> GetSavedInfo(long authKeyId, TLBytes q)
    {
        var result = SavedInfo.Builder().HasSavedCredentials(false).Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_CheckCanSendGift)]
    public ValueTask<TLBytes> CheckCanSendGift(long authKeyId, TLBytes q)
    {
        var result = CheckCanSendGiftResultOk.Builder().Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarGiftCollections)]
    public ValueTask<TLBytes> GetStarGiftCollections(long authKeyId, TLBytes q)
    {
        var result = StarGiftCollections.Builder()
            .Collections(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarGiftActiveAuctions)]
    public ValueTask<TLBytes> GetStarGiftActiveAuctions(long authKeyId, TLBytes q)
    {
        var result = StarGiftActiveAuctions.Builder()
            .Auctions(new Vector())
            .Users(new Vector())
            .Chats(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetResaleStarGifts)]
    public ValueTask<TLBytes> GetResaleStarGifts(long authKeyId, TLBytes q)
    {
        var result = ResaleStarGifts.Builder()
            .Count(0)
            .Gifts(new Vector())
            .NextOffset(""u8)
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetCraftStarGifts)]
    public ValueTask<TLBytes> GetCraftStarGifts(long authKeyId, TLBytes q)
    {
        var result = SavedStarGifts.Builder()
            .Count(0)
            .Gifts(new Vector())
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarsGiveawayOptions)]
    public ValueTask<TLBytes> GetStarsGiveawayOptions(long authKeyId, TLBytes q)
    {
        return ValueTask.FromResult(VectorBytes(new Vector()));
    }

    [TLFunction(Constructors.baseLayer_GetStarGiftUpgradePreview)]
    public ValueTask<TLBytes> GetStarGiftUpgradePreview(long authKeyId, TLBytes q)
    {
        byte[] input = q.AsSpan().ToArray();
        var request = new GetStarGiftUpgradePreview(input);
        var result = StarGiftUpgradePreview.Builder()
            .SampleAttributes(Ferrite.Services.Stars.StarGiftService.BuildAttributes(request.GiftId))
            .Prices(new Vector())
            .NextPrices(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetUniqueStarGiftValueInfo)]
    public ValueTask<TLBytes> GetUniqueStarGiftValueInfo(long authKeyId, TLBytes q)
    {
        var result = UniqueStarGiftValueInfo.Builder()
            .Currency("TON"u8)
            .Value(0)
            .InitialSaleDate(0)
            .InitialSaleStars(0)
            .InitialSalePrice(0)
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    private static TLBytes VectorBytes(Vector vector)
    {
        byte[] bytes = vector.ToReadOnlySpan().ToArray();
        return new TLBytes(bytes, 0, bytes.Length);
    }
}
