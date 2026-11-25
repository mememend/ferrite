// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Text;
using Ferrite.Data.Repositories;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.account;
using Ferrite.TL.baseLayer.dto;
using Ferrite.Utils;

namespace Ferrite.Services.Account;

/// <summary>
/// Seeds a couple of cloud themes so the chat theme picker is not empty.
/// Each theme points at one of the gradient wallpapers from <see cref="WallpaperSeed"/>.
/// </summary>
public sealed class ThemeSeed
{
    private sealed record BuiltInTheme(long Id, string Slug, string Title, int Accent,
        long WallpaperId, bool Night, string Emoticon);

    private static readonly BuiltInTheme[] BuiltIn =
    [
        new(9100001, "ferrite-day", "Ferrite Day", 0x3390EC, 9000001, false, "\u2600"),
        new(9100002, "ferrite-night", "Ferrite Night", 0x5288C1, 9000002, true, "\U0001F319"),
    ];

    private readonly IAccountSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public ThemeSeed(IAccountSettingsRepository repository, IUnitOfWork unitOfWork,
        TimeProvider time, ILogger log)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _time = time;
        _log = log;
    }

    public async ValueTask<int> RunAsync()
    {
        IReadOnlyCollection<TLThemeCatalogState> existing =
            await _repository.GetThemeCatalogAsync();
        int seeded = 0;
        try
        {
            var present = new HashSet<string>();
            foreach (TLThemeCatalogState row in existing)
            {
                using Theme theme = row.AsThemeCatalogState().Get_ThemeView().AsTheme();
                present.Add(Encoding.UTF8.GetString(theme.Slug));
            }
            foreach (BuiltInTheme built in BuiltIn)
            {
                if (present.Contains(built.Slug)) continue;
                TLWallpaperCatalogState? wallpaperRow =
                    await _repository.GetWallpaperCatalogAsync(built.WallpaperId);
                using (wallpaperRow)
                {
                    if (wallpaperRow is not { } row) continue;
                    WallPaperView wallpaperView = row.AsWallpaperCatalogState()
                        .Get_WallpaperView();
                    TLWallPaper? ownedWallpaper = null;
                    if (wallpaperView.Is(out WallPaperNoFile noFile))
                        ownedWallpaper = noFile.Clone().Build();
                    else if (wallpaperView.Is(out WallPaper wallpaper))
                        ownedWallpaper = wallpaper.Clone().Build();
                    if (ownedWallpaper is not { } wallpaperValue) continue;
                    using (wallpaperValue)
                    {
                        using TLTheme theme = BuildTheme(built, wallpaperValue);
                        using TLThemeCatalogState catalogRow = ThemeCatalogState.Builder()
                            .Theme(theme.AsSpan())
                            .OwnerUserId(0)
                            .Date((int)_time.GetUtcNow().ToUnixTimeSeconds())
                            .Build();
                        if (!_repository.PutThemeCatalog(catalogRow)) continue;
                        if (!await _unitOfWork.SaveAsync()) continue;
                        seeded++;
                    }
                }
            }
            _log.Information($"Theme seed: total={BuiltIn.Length}, seeded={seeded}");
            return seeded;
        }
        finally
        {
            foreach (TLThemeCatalogState row in existing) row.Dispose();
        }
    }

    private static TLTheme BuildTheme(BuiltInTheme built, TLWallPaper wallpaper)
    {
        using TLBaseTheme baseTheme = built.Night
            ? BaseThemeNight.Builder().Build()
            : BaseThemeClassic.Builder().Build();
        using TLThemeSettings settings = ThemeSettings.Builder()
            .BaseTheme(baseTheme.AsSpan())
            .AccentColor(built.Accent)
            .Wallpaper(wallpaper.AsSpan())
            .Build();
        Vector vector = new();
        vector.AppendTLObject(settings.AsSpan());
        return Theme.Builder()
            .Id(built.Id)
            .AccessHash(built.Id)
            .Slug(Encoding.UTF8.GetBytes(built.Slug))
            .Title(Encoding.UTF8.GetBytes(built.Title))
            .Settings(vector)
            .Emoticon(Encoding.UTF8.GetBytes(built.Emoticon))
            .Build();
    }
}
