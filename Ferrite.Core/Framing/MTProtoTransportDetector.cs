// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Buffers;
using System.Buffers.Binary;
using Autofac;
using DotNext.Buffers;
using Ferrite.Crypto;

namespace Ferrite.Core.Framing;

public class MTProtoTransportDetector : ITransportDetector
{
    const byte Abridged = 0xef;
    const int AbridgedInt = unchecked((int)0xefefefef);
    const int Intermediate = unchecked((int)0xeeeeeeee);
    const int PaddedIntermediate = unchecked((int)0xdddddddd);
    const int MaxFullFrameLength = 16 * 1024 * 1024;

    private readonly ILifetimeScope _scope;

    public MTProtoTransportDetector(ILifetimeScope scope)
    {
        _scope = scope;
    }

    public MTProtoTransport DetectTransport(ReadOnlySequence<byte> bytes,
        out IFrameDecoder? decoder, out IFrameEncoder? encoder,
        out SequencePosition sequencePosition)
    {
        decoder = null;
        encoder = null;
        sequencePosition = bytes.Start;
        if (bytes.Length == 0)
        {
            return MTProtoTransport.Unknown;
        }

        if (TryPeekFirstByte(bytes, out byte firstByte) && firstByte == Abridged)
        {
            decoder = new AbridgedFrameDecoder(_scope.Resolve<IMTProtoService>());
            encoder = new AbridgedFrameEncoder();
            sequencePosition = bytes.GetPosition(1);
            return MTProtoTransport.Abridged;
        }

        if (bytes.Length >= 8 &&
            TryReadInt32(bytes, 0, out int markerLow) &&
            TryReadInt32(bytes, 4, out int markerHigh) &&
            markerLow == Intermediate && markerHigh == Intermediate)
        {
            decoder = new FullFrameDecoder(_scope.Resolve<IMTProtoService>());
            encoder = new FullFrameEncoder();
            sequencePosition = bytes.GetPosition(8);
            return MTProtoTransport.Full;
        }

        if (bytes.Length >= 4 && TryReadInt32(bytes, 0, out int firstint))
        {
            if (firstint == Intermediate)
            {
                decoder = new IntermediateFrameDecoder(_scope.Resolve<IMTProtoService>());
                encoder = new IntermediateFrameEncoder();
                sequencePosition = bytes.GetPosition(4);
                return MTProtoTransport.Intermediate;
            }
            if (firstint == PaddedIntermediate)
            {
                decoder = new PaddedIntermediateFrameDecoder(_scope.Resolve<IMTProtoService>());
                encoder = new PaddedIntermediateFrameEncoder();
                sequencePosition = bytes.GetPosition(4);
                return MTProtoTransport.PaddedIntermediate;
            }
        }

        if (IsFullFrame(bytes))
        {
            decoder = new FullFrameDecoder(_scope.Resolve<IMTProtoService>());
            encoder = new FullFrameEncoder();
            sequencePosition = bytes.Start;
            return MTProtoTransport.Full;
        }

        if (bytes.Length < 64)
        {
            return MTProtoTransport.Unknown;
        }

        var payload = bytes.Slice(0, 64);
        var decryptionKey = payload.Slice(8, 32).ToArray();
        var decryptionIV = payload.Slice(40, 16).ToArray();
        var encryptionKey = payload.ToArray().Reverse().ToArray().AsSpan().Slice(8, 32).ToArray();
        var encryptionIV = payload.ToArray().Reverse().ToArray().AsSpan().Slice(40, 16).ToArray();

        Aes256Ctr decryptor = new Aes256Ctr(decryptionKey, decryptionIV);
        Aes256Ctr encryptor = new Aes256Ctr(encryptionKey, encryptionIV);

        MTProtoTransport transport;
        using (IMemoryOwner<byte> owner = UnmanagedMemoryPool<byte>.Shared.Rent(64))
        {
            Memory<byte> data = owner.Memory;
            decryptor.Transform(payload, data.Span);
            SpanReader<byte> rd = new SpanReader<byte>(data.Span.Slice(56));
            int identifier = rd.ReadInt32(true);
            if (identifier == AbridgedInt)
            {
                transport = MTProtoTransport.Abridged;
                decoder = new AbridgedFrameDecoder(decryptor, _scope.Resolve<IMTProtoService>());
                encoder = new AbridgedFrameEncoder(encryptor);
            }
            else if (identifier == Intermediate)
            {
                transport = MTProtoTransport.Intermediate;
                decoder = new IntermediateFrameDecoder(decryptor, _scope.Resolve<IMTProtoService>());
                encoder = new IntermediateFrameEncoder(encryptor);
            }
            else if (identifier == PaddedIntermediate)
            {
                transport = MTProtoTransport.PaddedIntermediate;
                decoder = new PaddedIntermediateFrameDecoder(decryptor, _scope.Resolve<IMTProtoService>());
                encoder = new PaddedIntermediateFrameEncoder(encryptor);
            }
            else
            {
                transport = MTProtoTransport.Full;
                decoder = new FullFrameDecoder(decryptor, _scope.Resolve<IMTProtoService>());
                encoder = new FullFrameEncoder(encryptor);
            }
        }

        sequencePosition = bytes.GetPosition(64);
        return transport;
    }

    private static bool IsFullFrame(ReadOnlySequence<byte> bytes)
    {
        if (bytes.Length < 8 || !TryReadInt32(bytes, 0, out int declared))
        {
            return false;
        }
        long length = declared;
        return length >= 12 && length <= MaxFullFrameLength && bytes.Length >= length;
    }

    private static bool TryPeekFirstByte(ReadOnlySequence<byte> bytes, out byte value)
    {
        var reader = new SequenceReader<byte>(bytes);
        return reader.TryPeek(out value);
    }

    private static bool TryReadInt32(ReadOnlySequence<byte> bytes, long offset, out int value)
    {
        value = 0;
        if (bytes.Length < offset + 4)
        {
            return false;
        }
        Span<byte> buffer = stackalloc byte[4];
        bytes.Slice(offset, 4).CopyTo(buffer);
        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }
}
