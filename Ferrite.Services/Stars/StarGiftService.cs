// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Runtime.InteropServices;
using System.Text;
using Ferrite.Data.Repositories;
using Ferrite.Services.Common;
using Ferrite.Services.Media;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.dto;
using Ferrite.TL.baseLayer.payments;

namespace Ferrite.Services.Stars;

/// <summary>
/// Local implementation of the Telegram Stars / star-gift methods.
/// Everything is free: the balance is local and gifts are stored in memory.
/// </summary>
public sealed class StarGiftService
{
    private const long LocalBalance = 1_000_000;

    private static readonly (long Id, long Stars, string Sticker)[] Catalog =
    {
        (5800001, 50, "gift1.webp"),
        (5800002, 100, "gift2.webp"),
        (5800003, 250, "gift3.webp"),
        (5800004, 500, "gift4.webp"),
    };

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly Dictionary<long, long> Forms = new();
    private static readonly Dictionary<long, int> PremiumForms = new();
    private static readonly Dictionary<long, List<byte[]>> Saved = new();
    private static volatile List<(long Id, long Stars, byte[] Bytes)>? _catalog;
    private static long _nextSavedId = 7_000_000;
    private static long _nextFormId = 1_000;
    private static long _nextUniqueId = 8_000_000;

    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly IUploadService _uploadService;
    private readonly IUserRepository _userRepository;
    private readonly IUnitOfWork _unitOfWork;

    public StarGiftService(IAuthorizationRepository authorizationRepository,
        IUploadService uploadService, IUserRepository userRepository,
        IUnitOfWork unitOfWork)
    {
        _authorizationRepository = authorizationRepository;
        _uploadService = uploadService;
        _userRepository = userRepository;
        _unitOfWork = unitOfWork;
    }

    [TLFunction(Constructors.baseLayer_GetStarsStatus)]
    public ValueTask<TLBytes> GetStatus(long authKeyId, TLBytes q)
    {
        using var balance = StarsAmount.Builder().Amount(LocalBalance).Nanos(0).Build();
        var status = StarsStatus.Builder()
            .Balance(balance.ToReadOnlySpan())
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(status.TLBytes!.Value);
    }

    // The client at layer 233 sends the gift withdrawal with a tmp_password, not an SRP
    // password, so it uses a different constructor id; without this it got a 500.
    [TLFunction(unchecked((int)0xc8ca817b))]
    public ValueTask<TLBytes> GetWithdrawalUrlTmpPassword(long authKeyId, TLBytes q)
        => GetWithdrawalUrl(authKeyId, q);

    [TLFunction(unchecked((int)0xec2030ae))]
    public ValueTask<TLBytes> WithdrawStarGift(long authKeyId, TLBytes q)
    {
        using var ok = BoolTrue.Builder().Build();
        return ValueTask.FromResult(ok.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarGiftWithdrawalUrl)]
    public ValueTask<TLBytes> GetWithdrawalUrl(long authKeyId, TLBytes q)
    {
        // No TON wallet exists on a local server, so the client only needs a page it can
        // open; fragment.com is where Telegram itself sends gift withdrawals.
        var result = StarGiftWithdrawalUrl.Builder()
            .Url("https://fragment.com/gifts"u8)
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarGiftAuctionState)]
    public async ValueTask<TLBytes> GetAuctionState(long authKeyId, TLBytes q)
    {
        var catalog = await EnsureCatalogAsync();
        (long giftId, long stars, byte[] giftBytes) = catalog[0];
        _ = giftId;
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var finished = Ferrite.TL.baseLayer.StarGiftAuctionStateFinished.Builder()
            .StartDate(now - 86_400)
            .EndDate(now)
            .AveragePrice(stars)
            .ListedCount(0)
            .FragmentListedCount(0)
            .Build();
        using var userState = Ferrite.TL.baseLayer.StarGiftAuctionUserState.Builder()
            .AcquiredCount(0)
            .Build();
        var result = Ferrite.TL.baseLayer.payments.PaymentsStarGiftAuctionState.Builder()
            .Gift(giftBytes)
            .State(finished.ToReadOnlySpan())
            .UserState(userState.ToReadOnlySpan())
            .Timeout(0)
            .Users(new Vector())
            .Chats(new Vector())
            .Build();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_GetStarGifts)]
    public async ValueTask<TLBytes> GetGifts(long authKeyId, TLBytes q)
    {
        var catalog = await EnsureCatalogAsync();
        Vector gifts = new();
        foreach ((_, _, byte[] bytes) in catalog)
            gifts.AppendTLObject(bytes);

        var result = StarGifts.Builder()
            .Hash(1)
            .Gifts(gifts)
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_GetPaymentForm)]
    public ValueTask<TLBytes> GetPaymentForm(long authKeyId, TLBytes q)
    {
        var request = new GetPaymentForm(q.AsSpan());
        if (TryPremiumInvoice(request.Invoice, out int months, out long premiumStars))
            return ValueTask.FromResult(BuildPremiumForm(months, premiumStars));
        long giftId = GiftIdFromInvoice(request.Invoice);
        long stars = StarsFor(giftId);

        long formId;
        lock (Gate)
        {
            formId = ++_nextFormId;
            Forms[formId] = giftId;
        }

        Vector prices = new();
        using (var price = LabeledPrice.Builder()
                   .Label("Star gift"u8)
                   .Amount(stars)
                   .Build())
            prices.AppendTLObject(price.ToReadOnlySpan());

        using var invoice = Invoice.Builder()
            .Currency("XTR"u8)
            .Prices(prices)
            .Build();

        var form = PaymentFormStarGift.Builder()
            .FormId(formId)
            .Invoice(invoice.ToReadOnlySpan())
            .Build();
        return ValueTask.FromResult(form.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_SendStarsForm)]
    public async ValueTask<TLBytes> SendStarsForm(long authKeyId, TLBytes q)
    {
        var request = new SendStarsForm(q.AsSpan());
        byte[] invoice = request.Invoice.ToArray();
        long formId = request.FormId;

        if (TryPremiumInvoice(invoice, out int months, out _))
        {
            long buyerId = await UserIdAsync(authKeyId);
            lock (Gate)
            {
                if (PremiumForms.TryGetValue(formId, out int known) && known > 0)
                    months = known;
            }
            await GrantPremiumAsync(buyerId);
            // Without the buyer in users the client keeps its cached non-premium user
            // until the next restart, so the Premium screen still looks unbought.
            Vector premiumUsers = new();
            TLUser? premium = _userRepository.GetUser(buyerId);
            using (premium)
            {
                if (premium is { } premiumUser)
                {
                    User premiumDto = premiumUser.AsUser();
                    using (var built = premiumDto.Clone().Build())
                        premiumUsers.AppendTLObject(built.ToReadOnlySpan());
                }
            }
            using var premiumUpdates = Ferrite.TL.baseLayer.Updates.Builder()
                .UpdatesProperty(new Vector())
                .Users(premiumUsers)
                .Chats(new Vector())
                .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                .Seq(0)
                .Build();
            var premiumResult = PaymentResult.Builder()
                .Updates(premiumUpdates.ToReadOnlySpan())
                .Build();
            return premiumResult.TLBytes!.Value;
        }

        long giftId = GiftIdFromInvoice(invoice);

        lock (Gate)
        {
            if (giftId == 0 && Forms.TryGetValue(request.FormId, out long known))
                giftId = known;
        }

        await EnsureCatalogAsync();
        long userId = await UserIdAsync(authKeyId);
        long recipientId = RecipientIdFromInvoice(invoice, userId);
        int date = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        StoreGift(recipientId, giftId, userId, date, unsaved: false);
        if (recipientId != userId)
            StoreGift(userId, giftId, userId, date, unsaved: true);

        using var updates = Ferrite.TL.baseLayer.Updates.Builder()
            .UpdatesProperty(new Vector())
            .Users(new Vector())
            .Chats(new Vector())
            .Date(date)
            .Seq(0)
            .Build();
        var result = PaymentResult.Builder()
            .Updates(updates.ToReadOnlySpan())
            .Build();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_GetSavedStarGifts)]
    public async ValueTask<TLBytes> GetSavedGifts(long authKeyId, TLBytes q)
    {
        long userId = await UserIdAsync(authKeyId);
        Vector gifts = new();
        Vector users = new();
        HashSet<long> known = new();
        int count;
        lock (Gate)
        {
            count = Saved.TryGetValue(userId, out List<byte[]>? list) ? list.Count : 0;
            if (count > 0)
                foreach (byte[] gift in Saved[userId])
                {
                    gifts.AppendTLObject(gift);
                    // The client shows "from <name>"; without the sender in users that
                    // row stays empty and the sheet has no peer to open.
                    using SavedStarGift parsed = ParseSaved(gift);
                    var peer = new PeerView(parsed.FromId);
                    if (peer.Is(out PeerUser sender) && known.Add(sender.UserId))
                    {
                        TLUser? stored = _userRepository.GetUser(sender.UserId);
                        using (stored)
                        {
                            if (stored is { } senderUser)
                                users.AppendTLObject(senderUser.AsUser().ToReadOnlySpan());
                        }
                    }
                }
        }

        var result = SavedStarGifts.Builder()
            .Count(count)
            .Gifts(gifts)
            .Chats(new Vector())
            .Users(users)
            .Build();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_SaveStarGift)]
    public async ValueTask<TLBytes> SaveGift(long authKeyId, TLBytes q)
    {
        (byte[] input, bool unsave) = ParseSaveGift(q);
        long userId = await UserIdAsync(authKeyId);
        lock (Gate)
        {
            if (Saved.TryGetValue(userId, out List<byte[]>? list))
            {
                int index = FindSaved(list, input);
                if (index >= 0)
                {
                    SavedStarGift current = ParseSaved(list[index]);
                    list[index] = WithFlags(list[index], unsave, current.PinnedToTop);
                }
            }
        }
        var ok = new BoolTrue();
        return ok.TLBytes!.Value;
    }

    private static (byte[] Input, bool Unsave) ParseSaveGift(TLBytes q)
    {
        var request = new SaveStarGift(q.AsSpan());
        return (request.Stargift.ToArray(), request.Unsave);
    }

    [TLFunction(Constructors.baseLayer_ToggleStarGiftsPinnedToTop)]
    public async ValueTask<TLBytes> TogglePinned(long authKeyId, TLBytes q)
    {
        List<byte[]> wanted = ParsePinnedGifts(q);
        long userId = await UserIdAsync(authKeyId);
        lock (Gate)
        {
            if (Saved.TryGetValue(userId, out List<byte[]>? list))
            {
                HashSet<int> pinned = new();
                foreach (byte[] input in wanted)
                {
                    int index = FindSaved(list, input);
                    if (index >= 0) pinned.Add(index);
                }
                for (int i = 0; i < list.Count; i++)
                {
                    SavedStarGift current = ParseSaved(list[i]);
                    bool pin = pinned.Contains(i);
                    if (current.PinnedToTop != pin)
                        list[i] = WithFlags(list[i], current.Unsaved, pin);
                }
            }
        }
        var updates = Ferrite.TL.baseLayer.Updates.Builder()
            .UpdatesProperty(new Vector())
            .Users(new Vector())
            .Chats(new Vector())
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Seq(0)
            .Build();
        return updates.TLBytes!.Value;
    }

    private static List<byte[]> ParsePinnedGifts(TLBytes q)
    {
        var request = new ToggleStarGiftsPinnedToTop(q.AsSpan());
        Vector source = request.Stargift;
        List<byte[]> result = new(source.Count);
        for (int i = 0; i < source.Count; i++)
            result.Add(source.ReadTLObject().ToArray());
        return result;
    }

    [TLFunction(Constructors.baseLayer_GetUniqueGiftChatThemes)]
    public ValueTask<TLBytes> GetUniqueGiftThemes(long authKeyId, TLBytes q)
    {
        var result = Ferrite.TL.baseLayer.account.ThemesNotModified.Builder().Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetMyBoosts)]
    public ValueTask<TLBytes> GetMyBoosts(long authKeyId, TLBytes q)
        => ValueTask.FromResult(EmptyMyBoosts());

    [TLFunction(Constructors.baseLayer_GetUserBoosts)]
    public ValueTask<TLBytes> GetUserBoosts(long authKeyId, TLBytes q)
        => ValueTask.FromResult(EmptyMyBoosts());

    [TLFunction(Constructors.baseLayer_GetBoostsList)]
    public ValueTask<TLBytes> GetBoostsList(long authKeyId, TLBytes q)
    {
        var result = Ferrite.TL.baseLayer.premium.BoostsList.Builder()
            .Count(0)
            .Boosts(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetBoostsStatus)]
    public ValueTask<TLBytes> GetBoostsStatus(long authKeyId, TLBytes q)
    {
        var result = Ferrite.TL.baseLayer.premium.BoostsStatus.Builder()
            .Level(0)
            .CurrentLevelBoosts(0)
            .Boosts(0)
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetConnectedStarRefBots)]
    public ValueTask<TLBytes> GetConnectedStarRefBots(long authKeyId, TLBytes q)
    {
        var result = ConnectedStarRefBots.Builder()
            .Count(0)
            .ConnectedBots(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    private static TLBytes EmptyMyBoosts()
    {
        var result = Ferrite.TL.baseLayer.premium.MyBoosts.Builder()
            .MyBoostsProperty(new Vector())
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return result.TLBytes!.Value;
    }

    private static SavedStarGift ParseSaved(byte[] bytes) => new(new TLBytes(bytes, 0, bytes.Length));

    private static byte[] WithFlags(byte[] bytes, bool unsaved, bool pinned)
    {
        SavedStarGift gift = ParseSaved(bytes);
        var rebuilt = SavedStarGift.Builder()
            .Date(gift.Date)
            .Gift(gift.Gift.ToArray())
            .SavedId(gift.SavedId)
            .FromId(gift.FromId.ToArray())
            .Unsaved(unsaved)
            .PinnedToTop(pinned)
            .MsgId(gift.MsgId)
            .ConvertStars(gift.ConvertStars)
            .UpgradeStars(gift.UpgradeStars)
            .CanUpgrade(gift.CanUpgrade)
            .Build();
        return rebuilt.ToReadOnlySpan().ToArray();
    }

    /// <summary>
    /// Maps one of the inputSavedStarGift* references the client sends to an entry in the
    /// stored list. Gifts are stored with msg_id = saved_id, so msg_id is a stable handle.
    /// </summary>
    private static int FindSaved(List<byte[]> list, ReadOnlySpan<byte> input)
    {
        if (input.Length < 4) return -1;
        int constructor = MemoryMarshal.Read<int>(input);
        if (constructor == Constructors.baseLayer_InputSavedStarGiftUser)
        {
            int msgId = new InputSavedStarGiftUser(input.ToArray()).MsgId;
            for (int i = 0; i < list.Count; i++)
                if (ParseSaved(list[i]).MsgId == msgId) return i;
            return -1;
        }
        if (constructor == Constructors.baseLayer_InputSavedStarGiftChat)
        {
            long savedId = new InputSavedStarGiftChat(input.ToArray()).SavedId;
            for (int i = 0; i < list.Count; i++)
                if (ParseSaved(list[i]).SavedId == savedId) return i;
            return -1;
        }
        if (constructor == Constructors.baseLayer_InputSavedStarGiftSlug)
        {
            string slug = Encoding.UTF8.GetString(new InputSavedStarGiftSlug(input.ToArray()).Slug);
            if (!slug.StartsWith("local-", StringComparison.Ordinal)
                || !long.TryParse(slug.AsSpan(6), out long giftId)) return -1;
            for (int i = 0; i < list.Count; i++)
                if (new StarGift(ParseSaved(list[i]).Gift.ToArray()).Id == giftId) return i;
            return -1;
        }
        return -1;
    }

    [TLFunction(Constructors.baseLayer_GetUniqueStarGift)]
    public ValueTask<TLBytes> GetUniqueGift(long authKeyId, TLBytes q)
    {
        var request = new GetUniqueStarGift(q.AsSpan());
        long giftId = ResolveGift(request.Slug);

        using var unique = StarGiftUnique.Builder()
            .Id(giftId + 900_000)
            .GiftId(giftId)
            .Title("Local collectible"u8)
            .Slug(Encoding.UTF8.GetBytes($"local-{giftId}"))
            .Num(1)
            .Attributes(BuildAttributes(giftId))
            .AvailabilityIssued(1)
            .AvailabilityTotal(1)
            .Build();

        var result = UniqueStarGift.Builder()
            .Gift(unique.ToReadOnlySpan())
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    /// <summary>
    /// Converts a saved gift into a unique (collectible) one. The client sends the same
    /// inputSavedStarGift reference it uses for saving, then reloads the list, so the
    /// stored entry has to carry a starGiftUnique instead of the plain starGift.
    /// </summary>
    [TLFunction(Constructors.baseLayer_UpgradeStarGift)]
    public async ValueTask<TLBytes> UpgradeGift(long authKeyId, TLBytes q)
    {
        byte[] input = q.AsSpan().ToArray();
        long userId = await UserIdAsync(authKeyId);
        var request = new UpgradeStarGift(input);
        bool upgraded = false;
        lock (Gate)
        {
            if (Saved.TryGetValue(userId, out List<byte[]>? list))
            {
                int index = FindSaved(list, request.Stargift.ToArray());
                if (index >= 0)
                {
                    SavedStarGift current = ParseSaved(list[index]);
                    long sourceId = SourceGiftId(current.Gift.ToArray());
                    long uniqueId = ++_nextUniqueId;
                    var rebuilt = SavedStarGift.Builder()
                        .Date(current.Date)
                        .Gift(BuildUniqueGift(uniqueId, sourceId))
                        .SavedId(current.SavedId)
                        .FromId(current.FromId.ToArray())
                        .Unsaved(current.Unsaved)
                        .PinnedToTop(current.PinnedToTop)
                        .MsgId(current.MsgId)
                        .Build();
                    list[index] = rebuilt.ToReadOnlySpan().ToArray();
                    upgraded = true;
                }
            }
        }
        if (!upgraded) return RpcErrorGenerator.GenerateError(400, "STARGIFT_INVALID"u8);

        using var updates = Ferrite.TL.baseLayer.Updates.Builder()
            .UpdatesProperty(new Vector())
            .Users(new Vector())
            .Chats(new Vector())
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Seq(0)
            .Build();
        return updates.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_CanPurchaseStore)]
    public ValueTask<TLBytes> CanPurchase(long authKeyId, TLBytes q)
    {
        var result = new BoolTrue();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }

    [TLFunction(Constructors.baseLayer_GetStarsTopupOptions)]
    public ValueTask<TLBytes> TopupOptions(long authKeyId, TLBytes q)
    {
        Vector options = new();
        return ValueTask.FromResult(VectorBytes(options));
    }

    [TLFunction(Constructors.baseLayer_GetStarsGiftOptions)]
    public ValueTask<TLBytes> GiftOptions(long authKeyId, TLBytes q)
    {
        Vector options = new();
        return ValueTask.FromResult(VectorBytes(options));
    }

    [TLFunction(Constructors.baseLayer_GetStarsSubscriptions)]
    public ValueTask<TLBytes> Subscriptions(long authKeyId, TLBytes q)
    {
        Vector subscriptions = new();
        return ValueTask.FromResult(VectorBytes(subscriptions));
    }

    [TLFunction(Constructors.baseLayer_GetStarsTransactions)]
    public ValueTask<TLBytes> Transactions(long authKeyId, TLBytes q)
    {
        Vector transactions = new();
        return ValueTask.FromResult(VectorBytes(transactions));
    }

    private static TLBytes VectorBytes(Vector vector)
    {
        byte[] bytes = vector.ToReadOnlySpan().ToArray();
        return new TLBytes(bytes, 0, bytes.Length);
    }

    private static long StarsFor(long giftId)
    {
        foreach ((long id, long stars, _) in Catalog)
            if (id == giftId) return stars;
        return Catalog[0].Stars;
    }

    private static long ResolveGift(ReadOnlySpan<byte> slug)
    {
        string text = Encoding.UTF8.GetString(slug);
        if (text.StartsWith("local-", StringComparison.Ordinal)
            && long.TryParse(text.AsSpan(6), out long id))
            return id;
        return Catalog[0].Id;
    }

    private static bool TryPremiumInvoice(ReadOnlySpan<byte> invoice, out int months,
        out long stars)
    {
        months = 0;
        stars = 0;
        if (invoice.Length < 4) return false;
        if (MemoryMarshal.Read<int>(invoice) !=
            Constructors.baseLayer_InputInvoicePremiumGiftCode) return false;
        var parsed = new InputInvoicePremiumGiftCode(invoice.ToArray());
        var view = parsed.Get_OptionView();
        if (!view.Is(out PremiumGiftCodeOption option)) return false;
        months = option.Months;
        stars = option.Amount;
        return months > 0 && stars > 0;
    }

    private static TLBytes BuildPremiumForm(int months, long stars)
    {
        long formId;
        lock (Gate)
        {
            formId = ++_nextFormId;
            PremiumForms[formId] = months;
        }
        Vector prices = new();
        using (var price = LabeledPrice.Builder()
                   .Label("Telegram Premium"u8)
                   .Amount(stars)
                   .Build())
            prices.AppendTLObject(price.ToReadOnlySpan());
        using var invoice = Invoice.Builder()
            .Currency("XTR"u8)
            .Prices(prices)
            .Build();
        var form = PaymentFormStarGift.Builder()
            .FormId(formId)
            .Invoice(invoice.ToReadOnlySpan())
            .Build();
        return form.TLBytes!.Value;
    }

    private ValueTask GrantPremiumAsync(long userId)
    {
        TLUser? stored = _userRepository.GetUser(userId);
        using (stored)
        {
            if (stored is not { } storedUser) return ValueTask.CompletedTask;
            User user = storedUser.AsUser();
            using (var updated = user.Clone().Premium(true).Build())
            {
                if (!_userRepository.PutUser(updated)) return ValueTask.CompletedTask;
            }
        }
        return new ValueTask(_unitOfWork.SaveAsync().AsTask());
    }

    private static long GiftIdFromInvoice(ReadOnlySpan<byte> invoice)
    {        if (invoice.Length < 4) return 0;
        int constructor = MemoryMarshal.Read<int>(invoice);
        if (constructor == Constructors.baseLayer_InputInvoiceStarGift)
            return new InputInvoiceStarGift(invoice.ToArray()).GiftId;
        if (constructor == Constructors.baseLayer_InputInvoiceStarGiftResale)
            return ResolveGift(new InputInvoiceStarGiftResale(invoice.ToArray()).Slug);
        return 0;
    }

    /// <summary>
    /// The recipient of a gift travels inside the invoice (inputInvoiceStarGift.peer).
    /// Without it the gift was stored under the sender, so the other side never got it.
    /// </summary>
    private static long RecipientIdFromInvoice(ReadOnlySpan<byte> invoice, long selfUserId)
    {
        if (invoice.Length < 4) return selfUserId;
        if (MemoryMarshal.Read<int>(invoice) != Constructors.baseLayer_InputInvoiceStarGift)
            return selfUserId;
        InputPeerView peer = new InputInvoiceStarGift(invoice.ToArray()).Get_PeerView();
        if (peer.Type is TLInputPeer.InputPeerType.InputPeerUser
            or TLInputPeer.InputPeerType.InputPeerUserFromMessage)
        {
            if (peer.Is(out InputPeerUser user) && user.UserId > 0) return user.UserId;
            if (peer.Is(out InputPeerUserFromMessage fromMessage) && fromMessage.UserId > 0)
                return fromMessage.UserId;
        }
        return selfUserId;
    }

    private static void StoreGift(long ownerId, long giftId, long senderId, int date, bool unsaved)
    {
        long savedId;
        lock (Gate) savedId = ++_nextSavedId;
        using TLPeer from = PeerUser.Builder().UserId(senderId).Build();
        using var saved = SavedStarGift.Builder()
            .Date(date)
            .Gift(GiftBytes(giftId))
            .SavedId(savedId)
            .MsgId((int)savedId)
            .FromId(from.AsSpan())
            .Unsaved(unsaved)
            .ConvertStars(StarsFor(giftId) / 2)
            .UpgradeStars(StarsFor(giftId))
            .CanUpgrade(true)
            .Build();
        byte[] bytes = saved.ToReadOnlySpan().ToArray();
        lock (Gate)
        {
            if (!Saved.TryGetValue(ownerId, out List<byte[]>? list))
                Saved[ownerId] = list = new List<byte[]>();
            list.Add(bytes);
        }
    }

    private async ValueTask<long> UserIdAsync(long authKeyId)
    {
        using TLAuthInfo? auth = await _authorizationRepository.GetAuthorizationAsync(authKeyId);
        return auth is not null && auth.Value.AsAuthInfo().LoggedIn
            ? auth.Value.AsAuthInfo().UserId : 0;
    }

    private static byte[] GiftBytes(long giftId)
    {
        var catalog = CatalogBytes();
        foreach ((long id, _, byte[] bytes) in catalog)
            if (id == giftId) return bytes;
        if (catalog.Count > 0) return catalog[0].Bytes;
        using var fallbackSticker = BuildSticker(Catalog[0].Id);
        byte[] stickerBytes = fallbackSticker.ToReadOnlySpan().ToArray();
        using var fallbackGift = BuildGift(Catalog[0].Id, Catalog[0].Stars, stickerBytes);
        return fallbackGift.AsSpan().ToArray();
    }

    private static List<(long Id, long Stars, byte[] Bytes)> CatalogBytes()
        => _catalog ?? new List<(long, long, byte[])>();

    /// <summary>
    /// Builds the catalogue once per process and registers each sticker as a downloadable
    /// document; without the registration the client cannot fetch the gift images and shows
    /// an empty gift sheet.
    /// </summary>
    private async ValueTask<List<(long Id, long Stars, byte[] Bytes)>> EnsureCatalogAsync()
    {
        if (_catalog is { } cached) return cached;
        await CatalogGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_catalog is { } built) return built;
            var list = new List<(long, long, byte[])>();
            foreach ((long id, long stars, string stickerFile) in Catalog)
            {
                byte[] stickerBytes;
                var registered = await _uploadService.RegisterBuiltinDocument(id * 10 + 1, id,
                    Encoding.UTF8.GetBytes($"local{id}"), "image/webp"u8.ToArray(),
                    StickerAttributes(), StickerContent(stickerFile)).ConfigureAwait(false);
                if (registered is { Success: true, Result: { } document })
                {
                    stickerBytes = document.AsSpan().ToArray();
                }
                else
                {
                    using var fallback = BuildSticker(id);
                    stickerBytes = fallback.ToReadOnlySpan().ToArray();
                }
                using var gift = BuildGift(id, stars, stickerBytes);
                list.Add((id, stars, gift.AsSpan().ToArray()));
            }
            return _catalog = list;
        }
        finally
        {
            CatalogGate.Release();
        }
    }

    private static byte[] StickerContent(string file)
    {
        using Stream? stream = typeof(StarGiftService).Assembly.GetManifestResourceStream(
            $"Ferrite.Services.Stars.stickers.{file}");
        if (stream is null) return [];
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] StickerAttributes()
    {
        Vector attributes = new();
        using (var empty = new InputStickerSetEmpty())
        using (var sticker = DocumentAttributeSticker.Builder()
                   .Alt(""u8)
                   .Stickerset(empty.ToReadOnlySpan())
                   .Build())
            attributes.AppendTLObject(sticker.ToReadOnlySpan());
        using (var size = DocumentAttributeImageSize.Builder().W(512).H(512).Build())
            attributes.AppendTLObject(size.ToReadOnlySpan());
        return attributes.ToReadOnlySpan().ToArray();
    }

    public static int UniqueGiftCount(long userId)
    {
        lock (Gate)
        {
            if (!Saved.TryGetValue(userId, out List<byte[]>? list)) return 0;
            int count = 0;
            foreach (byte[] entry in list)
            {
                SavedStarGift gift = ParseSaved(entry);
                ReadOnlySpan<byte> raw = gift.Gift.ToArray();
                if (raw.Length >= 4 && MemoryMarshal.Read<int>(raw) == 0x85f0a9cd) count++;
            }
            return count;
        }
    }

    private static long SourceGiftId(ReadOnlySpan<byte> gift)
    {
        if (gift.Length < 4) return Catalog[0].Id;
        int constructor = MemoryMarshal.Read<int>(gift);
        if (constructor == 0x85f0a9cd) return new StarGiftUnique(gift.ToArray()).GiftId;
        return new StarGift(gift.ToArray()).Id;
    }

    public static Vector BuildAttributes(long giftId)
    {
        var attributes = new Vector();
        using (var sticker = BuildSticker(giftId))
        using (var rare = StarGiftAttributeRarityRare.Builder().Build())
        {
            using (var model = StarGiftAttributeModel.Builder()
                       .Name("Model"u8)
                       .Document(sticker.ToReadOnlySpan())
                       .Rarity(rare.ToReadOnlySpan())
                       .Build())
                attributes.AppendTLObject(model.ToReadOnlySpan());
            using (var pattern = StarGiftAttributePattern.Builder()
                       .Name("Pattern"u8)
                       .Document(sticker.ToReadOnlySpan())
                       .Rarity(rare.ToReadOnlySpan())
                       .Build())
                attributes.AppendTLObject(pattern.ToReadOnlySpan());
        }
        using (var uncommon = StarGiftAttributeRarityUncommon.Builder().Build())
        using (var backdrop = StarGiftAttributeBackdrop.Builder()
                   .Name("Backdrop"u8)
                   .BackdropId((int)(giftId % 100_000))
                   .CenterColor(0x1f6feb)
                   .EdgeColor(0x0b1f3a)
                   .PatternColor(0x2f81f7)
                   .TextColor(0xffffff)
                   .Rarity(uncommon.ToReadOnlySpan())
                   .Build())
            attributes.AppendTLObject(backdrop.ToReadOnlySpan());
        return attributes;
    }

    private static byte[] BuildUniqueGift(long uniqueId, long sourceId)
    {
        using var unique = StarGiftUnique.Builder()
            .Id(uniqueId)
            .GiftId(sourceId)
            .Title("Local collectible"u8)
            .Slug(Encoding.UTF8.GetBytes($"local-{sourceId}-{uniqueId}"))
            .Num((int)(uniqueId % 100_000))
            .Attributes(BuildAttributes(sourceId))
            .AvailabilityIssued(1)
            .AvailabilityTotal(1)
            .Build();
        return unique.ToReadOnlySpan().ToArray();
    }

    private static Document BuildSticker(long giftId)
    {
        // Static WebP, not a gzipped TGS: the client ungzips "application/x-tgsticker"
        // and ends up drawing a black square for our plain webp bytes.
        string file = Catalog.FirstOrDefault(g => g.Id == giftId).Sticker ?? Catalog[0].Sticker;
        int size = StickerContent(file).Length;
        return Document.Builder()
            .Id(giftId * 10 + 1)
            .AccessHash(giftId)
            .FileReference(Encoding.UTF8.GetBytes($"local{giftId}"))
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .MimeType("image/webp"u8)
            .Size(size > 0 ? size : 1024)
            .DcId(1)
            .Attributes(new Vector(StickerAttributes().AsSpan()))
            .Build();
    }

    private static TLBytes BuildGift(long id, long stars, byte[] sticker)
    {
        var gift = StarGift.Builder()
            .Id(id)
            .Sticker(sticker)
            .Stars(stars)
            .ConvertStars(stars / 2)
            .Title(Encoding.UTF8.GetBytes($"Local gift {stars}"))
            .Limited(false)
            .Build();
        return gift.TLBytes!.Value;
    }
}
