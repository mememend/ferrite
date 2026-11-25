// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Text;
using Ferrite.Data.Repositories;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.messages;

namespace Ferrite.Services.Handlers.StickerMethods;

public sealed class GetCustomEmojiDocumentsHandler : StickerHandlerBase
{
    private readonly StickerDocumentIndex _documents;

    public GetCustomEmojiDocumentsHandler(IUnitOfWork unitOfWork,
        IAuthorizationRepository authorizationRepository,
        StickerDocumentIndex documents)
        : base(unitOfWork, authorizationRepository)
    {
        _documents = documents;
    }

    [TLFunction(Constructors.baseLayer_GetCustomEmojiDocuments)]
    public async Task<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        long[] ids = ((GetCustomEmojiDocuments)q).DocumentId.ToArray();
        if (await GetUserIdAsync(authKeyId) is null) return AuthError();

        // An empty answer is fatal for exteraGram: AnimatedEmojiDrawable keeps the
        // unanswered ids in loadedFromServer and requests them again from the
        // response callback, so a missing document turns into an endless loop.
        // Answer exactly one document per requested id.
        byte[]?[] stored = new byte[ids.Length][];
        int resolved = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            using TLDocument? document = await _documents.GetDocumentByIdAsync(ids[i]);
            if (document is null) continue;
            stored[i] = document.Value.AsDocument().ToReadOnlySpan().ToArray();
            resolved++;
        }
        Console.WriteLine($"[emoji-docs] requested={ids.Length} resolved={resolved} " +
                          $"ids={string.Join(',', ids.Take(6))}");
        return BuildAnswer(ids, stored);
    }

    private static TLBytes BuildAnswer(long[] ids, byte[]?[] stored)
    {
        var vector = new Vector();
        for (int i = 0; i < ids.Length; i++)
        {
            if (stored[i] is { } bytes)
            {
                vector.AppendTLObject(bytes);
                continue;
            }
            using Document placeholder = BuildPlaceholder(ids[i]);
            vector.AppendTLObject(placeholder.ToReadOnlySpan());
        }
        byte[] answer = vector.ToReadOnlySpan().ToArray();
        return new TLBytes(answer, 0, answer.Length);
    }

    private static Document BuildPlaceholder(long id)
    {
        Vector attributes = new();
        using (var empty = new InputStickerSetEmpty())
        using (var emoji = DocumentAttributeCustomEmoji.Builder()
                   .Free(true).TextColor(false)
                   .Alt(Array.Empty<byte>())
                   .Stickerset(empty.ToReadOnlySpan()).Build())
            attributes.AppendTLObject(emoji.ToReadOnlySpan());
        using (var size = DocumentAttributeImageSize.Builder()
                   .W(512).H(512).Build())
            attributes.AppendTLObject(size.ToReadOnlySpan());
        return Document.Builder()
            .Id(id)
            .AccessHash(id)
            .FileReference(Encoding.UTF8.GetBytes($"emoji{id}"))
            .Date((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .MimeType("image/webp"u8)
            .Size(1024)
            .DcId(1)
            .Attributes(new Vector(attributes.ToReadOnlySpan().ToArray()))
            .Build();
    }
}
