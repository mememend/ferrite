// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Ferrite.Data.Repositories;
using Ferrite.Services.Media;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.dto;
using Ferrite.TL.baseLayer.stories;
using Serilog;

namespace Ferrite.Services.Stories;

/// <summary>
/// Local stories built on the existing uploaded-media pipeline.
/// State lives in memory: a server restart clears stories, like a fresh local instance.
/// </summary>
public sealed class StoryRecord
{
    public int Id;
    public int Date;
    public int ExpireDate;
    public byte[] Media = [];
    public string? Caption;
    public int Views;
    public bool Out;
    public bool Pinned;
    public bool Noforwards;
}

internal static class StoryStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<long, List<StoryRecord>> ByOwner = new();
    private static readonly Dictionary<long, int> MaxReadId = new();
    private static int _nextId = 5000;

    public static StoryRecord Add(long ownerId, byte[] media, string? caption, int period,
        bool pinned, bool noforwards)
    {
        lock (Gate)
        {
            int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var story = new StoryRecord
            {
                Id = Interlocked.Increment(ref _nextId),
                Date = now,
                ExpireDate = now + (period > 0 ? period : 86400),
                Media = media,
                Caption = caption,
                Views = 1,
                Out = true,
                Pinned = pinned,
                Noforwards = noforwards
            };
            if (!ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list))
                ByOwner[ownerId] = list = new List<StoryRecord>();
            list.Add(story);
            return story;
        }
    }

    public static List<StoryRecord> Active(long ownerId)
    {
        lock (Gate)
        {
            if (!ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)) return [];
            int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return list.Where(s => s.ExpireDate > now).ToList();
        }
    }

    public static List<StoryRecord> All(long ownerId)
    {
        lock (Gate)
        {
            return ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)
                ? list.ToList()
                : [];
        }
    }

    public static Dictionary<long, List<StoryRecord>> ActiveByPeer()
    {
        lock (Gate)
        {
            int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result = new Dictionary<long, List<StoryRecord>>();
            foreach ((long owner, List<StoryRecord> list) in ByOwner)
            {
                List<StoryRecord> active = list.Where(s => s.ExpireDate > now).ToList();
                if (active.Count > 0) result[owner] = active;
            }

            return result;
        }
    }

    public static StoryRecord? Find(long ownerId, int id)
    {
        lock (Gate)
        {
            return ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)
                ? list.FirstOrDefault(s => s.Id == id)
                : null;
        }
    }

    public static List<StoryRecord> ByIds(long ownerId, IEnumerable<int> ids)
    {
        var wanted = ids.ToHashSet();
        lock (Gate)
        {
            if (!ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)) return [];
            return list.Where(s => wanted.Contains(s.Id)).ToList();
        }
    }

    public static int MaxRead(long ownerId)
    {
        lock (Gate) return MaxReadId.TryGetValue(ownerId, out int id) ? id : 0;
    }

    public static void SetMaxRead(long ownerId, int maxId)
    {
        lock (Gate)
        {
            if (!MaxReadId.TryGetValue(ownerId, out int current) || maxId > current)
                MaxReadId[ownerId] = maxId;
        }
    }

    public static List<int> Delete(long ownerId, IEnumerable<int> ids)
    {
        var removed = new List<int>();
        lock (Gate)
        {
            if (!ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)) return removed;
            foreach (int id in ids.Distinct())
            {
                StoryRecord? story = list.FirstOrDefault(s => s.Id == id);
                if (story == null) continue;
                list.Remove(story);
                removed.Add(id);
            }
        }

        return removed;
    }

    public static void IncrementViews(long ownerId, IEnumerable<int> ids)
    {
        lock (Gate)
        {
            if (!ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)) return;
            var wanted = ids.ToHashSet();
            foreach (StoryRecord story in list.Where(s => wanted.Contains(s.Id)))
                story.Views++;
        }
    }

    public static bool EditCaption(long ownerId, int id, string? caption)
    {
        lock (Gate)
        {
            StoryRecord? story = ByOwner.TryGetValue(ownerId, out List<StoryRecord>? list)
                ? list.FirstOrDefault(s => s.Id == id)
                : null;
            if (story == null) return false;
            story.Caption = caption;
            return true;
        }
    }
}

public abstract class StoriesHandlerBase
{
    protected readonly IAuthorizationRepository Authorization;

    protected StoriesHandlerBase(IAuthorizationRepository authorization)
    {
        Authorization = authorization;
    }

    protected StoriesHandlerBase()
    {
        Authorization = null!;
    }

    protected async Task<long?> GetUserIdAsync(long authKeyId)
    {
        using TLAuthInfo? auth = await Authorization.GetAuthorizationAsync(authKeyId);
        return auth?.AsAuthInfo().UserId;
    }

    protected static TLBytes AuthError() =>
        RpcErrorGenerator.GenerateError(401, "AUTH_KEY_INVALID"u8);

    protected static TLBytes PeerInvalid() =>
        RpcErrorGenerator.GenerateError(400, "PEER_ID_INVALID"u8);

    protected static byte[] PeerBytes(long ownerId)
    {
        using PeerUser peer = PeerUser.Builder().UserId(ownerId).Build();
        return peer.TLBytes!.Value.AsSpan().ToArray();
    }

    protected static TLBytes AsTl(byte[] bytes) => new(bytes, 0, bytes.Length);

    private static Vector PrivacyVector()
    {
        using PrivacyValueAllowAll allowAll = PrivacyValueAllowAll.Builder().Build();
        var privacy = new Vector();
        privacy.AppendTLObject(allowAll.TLBytes!.Value.AsSpan());
        return privacy;
    }

    protected static TLBytes Unchanged() =>
        BoolTrue.Builder().Build().TLBytes!.Value;

    protected static byte[] BuildStoryItem(StoryRecord story, long ownerId)
    {
        using StoryViews views = StoryViews.Builder()
            .HasViewers(true)
            .ViewsCount(story.Views)
            .Build();
        StoryItem.TLObjectBuilder builder = StoryItem.Builder()
            .OutProperty(story.Out)
            .Pinned(story.Pinned)
            .Noforwards(story.Noforwards)
            .Id(story.Id)
            .Date(story.Date)
            .FromId(PeerBytes(ownerId))
            .ExpireDate(story.ExpireDate)
            .Media(story.Media)
            .Privacy(PrivacyVector())
            .Views(views.TLBytes!.Value.AsSpan());
        if (!string.IsNullOrEmpty(story.Caption))
            builder = builder.Caption(Encoding.UTF8.GetBytes(story.Caption));
        using StoryItem item = builder.Build();
        return item.TLBytes!.Value.AsSpan().ToArray();
    }

    protected static TLBytes BuildStoriesResponse(long ownerId, IEnumerable<StoryRecord> stories)
    {
        List<StoryRecord> list = stories.ToList();
        var items = new Vector();
        foreach (StoryRecord story in list)
            items.AppendTLObject(BuildStoryItem(story, ownerId));
        Ferrite.TL.baseLayer.stories.Stories response =
            Ferrite.TL.baseLayer.stories.Stories.Builder()
            .Count(list.Count)
            .StoriesProperty(items)
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return response.TLBytes!.Value;
    }

    protected static byte[] BuildPeerStoriesCore(long ownerId, List<StoryRecord> active)
    {
        var items = new Vector();
        foreach (StoryRecord story in active)
            items.AppendTLObject(BuildStoryItem(story, ownerId));
        using PeerStories core = PeerStories.Builder()
            .Peer(PeerBytes(ownerId))
            .MaxReadId(StoryStore.MaxRead(ownerId))
            .Stories(items)
            .Build();
        return core.TLBytes!.Value.AsSpan().ToArray();
    }

    protected static TLBytes BuildPeerStories(long ownerId, List<StoryRecord> active)
    {
        StoriesPeerStories wrapper = StoriesPeerStories.Builder()
            .Stories(BuildPeerStoriesCore(ownerId, active))
            .Chats(new Vector())
            .Users(new Vector())
            .Build();
        return wrapper.TLBytes!.Value;
    }

    protected static TLBytes BuildUpdates(byte[] storyBytes, long ownerId)
    {
        var updates = new Vector();
        using (UpdateStory update = UpdateStory.Builder()
                   .Peer(PeerBytes(ownerId))
                   .Story(storyBytes)
                   .Build())
        {
            updates.AppendTLObject(update.TLBytes!.Value.AsSpan());
        }

        Ferrite.TL.baseLayer.Updates result = Ferrite.TL.baseLayer.Updates.Builder()
            .UpdatesProperty(updates)
            .Users(new Vector())
            .Chats(new Vector())
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Seq(0)
            .Build();
        return result.TLBytes!.Value;
    }

    protected static TLBytes BuildEmptyUpdates()
    {
        Ferrite.TL.baseLayer.Updates result = Ferrite.TL.baseLayer.Updates.Builder()
            .UpdatesProperty(new Vector())
            .Users(new Vector())
            .Chats(new Vector())
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Seq(0)
            .Build();
        return result.TLBytes!.Value;
    }
}
