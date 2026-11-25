// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Ferrite.Data.Repositories;
using Ferrite.Services.Media;
using Ferrite.Services.Users;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.stories;

namespace Ferrite.Services.Stories;

public sealed class SendStoryHandler : StoriesHandlerBase
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUploadService _upload;
    private readonly IPhotoProcessingService _photos;
    private readonly IPhotoRepository _photoRepository;
    private readonly IDocumentsRepository _documentsRepository;

    public SendStoryHandler(IAuthorizationRepository authorization, IUnitOfWork unitOfWork,
        IUploadService upload, IPhotoProcessingService photos, IPhotoRepository photoRepository,
        IDocumentsRepository documentsRepository)
        : base(authorization)
    {
        _unitOfWork = unitOfWork;
        _upload = upload;
        _photos = photos;
        _photoRepository = photoRepository;
        _documentsRepository = documentsRepository;
    }

    [TLFunction(Constructors.baseLayer_SendStory)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        (byte[] inputMedia, string? caption, int period, bool pinned, bool noforwards) =
            ParseSendStory(q);
        MediaResolver.MediaResolution media = await MediaResolver.ResolveAsync(
            inputMedia, _upload, _photos, _unitOfWork, _photoRepository, _documentsRepository);
        if (media.Error != null || media.MediaBytes == null)
            return RpcErrorGenerator.GenerateError(400, "MEDIA_INVALID"u8);
        StoryRecord story = StoryStore.Add(userId.Value, media.MediaBytes, caption, period,
            pinned, noforwards);
        return BuildUpdates(BuildStoryItem(story, userId.Value), userId.Value);
    }

    private static (byte[] Media, string? Caption, int Period, bool Pinned, bool Noforwards)
        ParseSendStory(TLBytes q)
    {
        var request = new SendStory(q.AsSpan());
        return (request.Media.ToArray(),
            request.Flags[0] ? Encoding.UTF8.GetString(request.Caption) : null,
            request.Flags[3] ? request.Period : 86400,
            request.Pinned,
            request.Noforwards);
    }
}

public sealed class GetPeerStoriesHandler : StoriesHandlerBase
{
    public GetPeerStoriesHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_GetPeerStories)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new GetPeerStories(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        if (ownerId == 0) return PeerInvalid();
        return BuildPeerStories(ownerId, StoryStore.Active(ownerId));
    }
}

public sealed class GetAllStoriesHandler : StoriesHandlerBase
{
    public GetAllStoriesHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_GetAllStories)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var peerStories = new Vector();
        int count = 0;
        foreach ((long owner, List<StoryRecord> active) in StoryStore.ActiveByPeer())
        {
            peerStories.AppendTLObject(BuildPeerStoriesCore(owner, active));
            count += active.Count;
        }

        using StoriesStealthMode stealth = StoriesStealthMode.Builder().Build();
        AllStories response = AllStories.Builder()
            .HasMore(false)
            .Count(count)
            .State(ReadOnlySpan<byte>.Empty)
            .PeerStories(peerStories)
            .Chats(new Vector())
            .Users(new Vector())
            .StealthMode(stealth.TLBytes!.Value.AsSpan())
            .Build();
        return response.TLBytes!.Value;
    }
}

public sealed class GetStoriesArchiveHandler : StoriesHandlerBase
{
    public GetStoriesArchiveHandler(IAuthorizationRepository authorization)
        : base(authorization) { }

    [TLFunction(Constructors.baseLayer_GetStoriesArchive)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new GetStoriesArchive(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        return BuildStoriesResponse(ownerId, StoryStore.All(ownerId));
    }
}

public sealed class GetPinnedStoriesHandler : StoriesHandlerBase
{
    public GetPinnedStoriesHandler(IAuthorizationRepository authorization)
        : base(authorization) { }

    [TLFunction(Constructors.baseLayer_GetPinnedStories)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new GetPinnedStories(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        List<StoryRecord> pinned = StoryStore.All(ownerId).Where(s => s.Pinned).ToList();
        return BuildStoriesResponse(ownerId, pinned);
    }
}

public sealed class GetStoriesByIdHandler : StoriesHandlerBase
{
    public GetStoriesByIdHandler(IAuthorizationRepository authorization)
        : base(authorization) { }

    [TLFunction(Constructors.baseLayer_GetStoriesByID)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new GetStoriesByID(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        return BuildStoriesResponse(ownerId, StoryStore.ByIds(ownerId, request.Id.ToArray()));
    }
}

public sealed class ReadStoriesHandler : StoriesHandlerBase
{
    public ReadStoriesHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_ReadStories)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new ReadStories(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        int maxId = request.MaxId;
        StoryStore.SetMaxRead(ownerId, maxId);
        var ids = new VectorOfInt();
        ids.Append(maxId);
        return AsTl(ids.ToReadOnlySpan().ToArray());
    }
}

public sealed class IncrementStoryViewsHandler : StoriesHandlerBase
{
    public IncrementStoryViewsHandler(IAuthorizationRepository authorization)
        : base(authorization) { }

    [TLFunction(Constructors.baseLayer_IncrementStoryViews)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new IncrementStoryViews(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        StoryStore.IncrementViews(ownerId, request.Id.ToArray());
        return Unchanged();
    }
}

public sealed class DeleteStoriesHandler : StoriesHandlerBase
{
    public DeleteStoriesHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_DeleteStories)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new DeleteStories(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        List<int> removed = StoryStore.Delete(ownerId, request.Id.ToArray());
        var ids = new VectorOfInt();
        foreach (int id in removed) ids.Append(id);
        return AsTl(ids.ToReadOnlySpan().ToArray());
    }
}

public sealed class EditStoryHandler : StoriesHandlerBase
{
    public EditStoryHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_EditStory)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new EditStory(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        string? caption = request.Flags[1] ? Encoding.UTF8.GetString(request.Caption) : null;
        StoryStore.EditCaption(ownerId, request.Id, caption);
        StoryRecord? story = StoryStore.Find(ownerId, request.Id);
        if (story == null) return RpcErrorGenerator.GenerateError(400, "STORY_ID_INVALID"u8);
        return BuildUpdates(BuildStoryItem(story, ownerId), ownerId);
    }
}

public sealed class ToggleAllStoriesHiddenHandler : StoriesHandlerBase
{
    public ToggleAllStoriesHiddenHandler() { }

    [TLFunction(Constructors.baseLayer_ToggleAllStoriesHidden)]
    public Task<TLBytes> Handle(long authKeyId, TLBytes q) => Task.FromResult(Unchanged());
}

public sealed class CanSendStoryHandler : StoriesHandlerBase
{
    public CanSendStoryHandler() { }

    [TLFunction(Constructors.baseLayer_CanSendStory)]
    public Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        CanSendStoryCount count = CanSendStoryCount.Builder().CountRemains(100).Build();
        return Task.FromResult(count.TLBytes!.Value);
    }
}

public sealed class ExportStoryLinkHandler : StoriesHandlerBase
{
    public ExportStoryLinkHandler(IAuthorizationRepository authorization) : base(authorization) { }

    [TLFunction(Constructors.baseLayer_ExportStoryLink)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new ExportStoryLink(q.AsSpan());
        ExportedStoryLink link = ExportedStoryLink.Builder()
            .Link(Encoding.UTF8.GetBytes($"https://t.me/c/{userId.Value}/{request.Id}"))
            .Build();
        return link.TLBytes!.Value;
    }
}

public sealed class GetStoriesAlbumsHandler : StoriesHandlerBase
{
    public GetStoriesAlbumsHandler() { }

    [TLFunction(Constructors.baseLayer_GetAlbums)]
    public Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        AlbumsNotModified albums = AlbumsNotModified.Builder().Build();
        return Task.FromResult(albums.TLBytes!.Value);
    }
}

public sealed class ActivateStealthModeHandler : StoriesHandlerBase
{
    public ActivateStealthModeHandler() { }

    [TLFunction(Constructors.baseLayer_ActivateStealthMode)]
    public Task<TLBytes> Handle(long authKeyId, TLBytes q) =>
        Task.FromResult(BuildEmptyUpdates());
}

public sealed class TogglePinnedStoriesHandler : StoriesHandlerBase
{
    public TogglePinnedStoriesHandler(IAuthorizationRepository authorization)
        : base(authorization) { }

    [TLFunction(Constructors.baseLayer_TogglePinned)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long? userId = await GetUserIdAsync(authKeyId);
        if (!userId.HasValue) return AuthError();
        var request = new TogglePinned(q.AsSpan());
        (TLPeer.PeerType _, long ownerId) =
            PeerResolver.ResolveHistoryPeer(request.Get_PeerView(), userId.Value);
        StoryRecord? story = request.Id.ToArray().Select(id => StoryStore.Find(ownerId, id))
            .FirstOrDefault(s => s != null);
        if (story != null) story.Pinned = request.Pinned;
        var ids = new VectorOfInt();
        if (story != null) ids.Append(story.Id);
        return AsTl(ids.ToReadOnlySpan().ToArray());
    }
}
