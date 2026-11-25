// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Collections.Concurrent;
using Ferrite.Data.Repositories;
using Ferrite.Services.Users;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.account;
using Vector = Ferrite.TL.Vector;

namespace Ferrite.Services.Business;

/// <summary>
/// Local implementation of the business chat-bot methods. Connections are kept in
/// memory, so the "Chat bots" section opens and works instead of failing with
/// METHOD_DISABLED.
/// </summary>
public sealed class ConnectedBotService
{
    private sealed record Entry(long BotId, long Date);

    private static readonly ConcurrentDictionary<long, List<Entry>> Connections = new();
    private static readonly ConcurrentDictionary<long, HashSet<long>> PausedPeers = new();

    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly IUserRepository _userRepository;
    private readonly UserSerializer _userSerializer;

    public ConnectedBotService(IAuthorizationRepository authorizationRepository, IUserRepository userRepository,
        UserSerializer userSerializer)
    {
        _authorizationRepository = authorizationRepository;
        _userRepository = userRepository;
        _userSerializer = userSerializer;
    }

    [TLFunction(Constructors.baseLayer_GetConnectedBots)]
    public async ValueTask<TLBytes> GetConnectedBots(long authKeyId, TLBytes q)
    {
        var auth = await _authorizationRepository.GetAuthorizationAsync(authKeyId);
        if (auth == null) return RpcErrorGenerator.GenerateError(400, "AUTH_KEY_INVALID"u8);
        long userId = auth.Value.AsAuthInfo().UserId;

        var bots = new Vector();
        var users = new Vector();
        if (Connections.TryGetValue(userId, out List<Entry>? list))
        {
            List<Entry> snapshot;
            lock (list) snapshot = [..list];
            foreach (Entry entry in snapshot)
            {
                using var recipients = BusinessBotRecipients.Builder().Build();
                using var rights = BusinessBotRights.Builder().Build();
                using var bot = ConnectedBot.Builder()
                    .BotId(entry.BotId)
                    .Recipients(recipients.ToReadOnlySpan())
                    .Rights(rights.ToReadOnlySpan())
                    .Date((int)entry.Date)
                    .Build();
                bots.AppendTLObject(bot.ToReadOnlySpan());

                TLUser? user = _userRepository.GetUser(entry.BotId);
                if (user == null) continue;
                using TLUser serialized = _userSerializer.WithStatus(userId, user.Value);
                users.AppendTLObject(serialized.AsSpan());
            }
        }

        var result = ConnectedBots.Builder().ConnectedBotsProperty(bots).Users(users).Build();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_UpdateConnectedBot)]
    public async ValueTask<TLBytes> UpdateConnectedBot(long authKeyId, TLBytes q)
    {
        var auth = await _authorizationRepository.GetAuthorizationAsync(authKeyId);
        if (auth == null) return RpcErrorGenerator.GenerateError(400, "AUTH_KEY_INVALID"u8);
        long userId = auth.Value.AsAuthInfo().UserId;

        var request = new UpdateConnectedBot(q.AsSpan());
        bool deleted = request.Deleted;
        long botId = ResolveUserId((InputUserView)request.Bot, userId);
        if (botId != 0)
        {
            List<Entry> list = Connections.GetOrAdd(userId, _ => []);
            lock (list)
            {
                list.RemoveAll(x => x.BotId == botId);
                if (!deleted) list.Add(new Entry(botId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
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

    [TLFunction(Constructors.baseLayer_ToggleConnectedBotPaused)]
    public async ValueTask<TLBytes> ToggleConnectedBotPaused(long authKeyId, TLBytes q)
    {
        var auth = await _authorizationRepository.GetAuthorizationAsync(authKeyId);
        if (auth == null) return RpcErrorGenerator.GenerateError(400, "AUTH_KEY_INVALID"u8);
        long userId = auth.Value.AsAuthInfo().UserId;

        var request = new ToggleConnectedBotPaused(q.AsSpan());
        long peerId = ResolvePeerUserId((InputPeerView)request.Peer, userId);
        bool paused = request.Paused;
        if (peerId != 0)
        {
            HashSet<long> pausedPeers = PausedPeers.GetOrAdd(userId, _ => []);
            lock (pausedPeers)
            {
                if (paused) pausedPeers.Add(peerId);
                else pausedPeers.Remove(peerId);
            }
        }

        using var result = new BoolTrue();
        return result.TLBytes!.Value;
    }

    [TLFunction(Constructors.baseLayer_DisablePeerConnectedBot)]
    public async ValueTask<TLBytes> DisablePeerConnectedBot(long authKeyId, TLBytes q)
    {
        var auth = await _authorizationRepository.GetAuthorizationAsync(authKeyId);
        if (auth == null) return RpcErrorGenerator.GenerateError(400, "AUTH_KEY_INVALID"u8);

        using var result = new BoolTrue();
        return result.TLBytes!.Value;
    }

    private static long ResolveUserId(InputUserView user, long selfUserId)
    {
        if (user.Is(out InputUser inputUser)) return inputUser.UserId;
        if (user.Is(out InputUserFromMessage fromMessage)) return fromMessage.UserId;
        if (user.Is(out InputUserSelf _)) return selfUserId;
        return 0;
    }

    private static long ResolvePeerUserId(InputPeerView peer, long selfUserId)
    {
        if (peer.Is(out InputPeerUser user)) return user.UserId;
        if (peer.Is(out InputPeerSelf _)) return selfUserId;
        return 0;
    }
}
