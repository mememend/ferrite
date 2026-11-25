// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using Ferrite.Data.Repositories;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.account;
using Ferrite.TL.baseLayer.dto;
using Ferrite.Utils;

namespace Ferrite.Services.Account;

/// <summary>
/// Fills the global wallpaper catalogue with gradient wallpapers.
/// An empty catalogue leaves the client without any chat background,
/// which shows up as a plain black square.
/// </summary>
public sealed class WallpaperSeed
{
    private static readonly (long Id, int First, int Second, int Third, int Fourth)[]
        BuiltIn =
        [
            (9000001, 0x1E2B4D, 0x3F5C8C, 0x5C7FB8, 0xA8C6E8),
            (9000002, 0x2B1E3D, 0x4E3A6B, 0x7A5C9C, 0xD0B8E8),
            (9000003, 0x1E3D2B, 0x3A6B4E, 0x5C9C7A, 0xB8E8D0),
            (9000004, 0x3D2B1E, 0x6B4E3A, 0x9C7A5C, 0xE8D0B8),
            (9000005, 0x1E3038, 0x33565E, 0x4C7F8F, 0xA6D4E0),
            (9000006, 0x33202B, 0x5E3344, 0x8C5A72, 0xE0B6C6),
        ];

    private readonly IAccountSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public WallpaperSeed(IAccountSettingsRepository repository,
        IUnitOfWork unitOfWork, TimeProvider time, ILogger log)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _time = time;
        _log = log;
    }

    public async ValueTask<int> RunAsync()
    {
        int seeded = 0;
        // Always rewrite the built-in rows: a row stored by an earlier seed has no
        // colours, which the client renders as a black square (and can crash on).
        foreach ((long id, int first, int second, int third, int fourth) in BuiltIn)
        {
            using TLWallPaperSettings settings = WallPaperSettings.Builder()
                .BackgroundColor(first)
                .SecondBackgroundColor(second)
                .ThirdBackgroundColor(third)
                .FourthBackgroundColor(fourth)
                .Intensity(60)
                .Rotation(45)
                .Build();
            using TLWallPaper wallpaper = WallPaperNoFile.Builder()
                .Id(id)
                .Settings(settings.AsSpan())
                .Build();
            using TLWallpaperCatalogState row = WallpaperCatalogState.Builder()
                .Wallpaper(wallpaper.AsSpan())
                .OwnerUserId(0)
                .Date((int)_time.GetUtcNow().ToUnixTimeSeconds())
                .Build();
            if (!_repository.PutWallpaperCatalog(row)) continue;
            if (!await _unitOfWork.SaveAsync()) continue;
            seeded++;
        }
        _log.Information($"Wallpaper seed: total={BuiltIn.Length}, seeded={seeded}");
        return seeded;
    }
}
