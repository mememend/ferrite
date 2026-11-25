// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Text;
using Ferrite.TL;
using Ferrite.TL.baseLayer.help;

namespace Ferrite.Services.Handlers.HelpMethods;

/// <summary>
/// Serves the full country/phone-code table from the bundled countries.txt,
/// so the login screen can pick any country instead of a single hardcoded one.
/// </summary>
public sealed class GetCountriesListHandler
{
    private static readonly object Gate = new();
    private static byte[]? _cached;
    private static int _hash;

    [TLFunction(Constructors.baseLayer_GetCountriesList)]
    public ValueTask<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        lock (Gate)
        {
            _cached ??= Build();
        }
        byte[] bytes = _cached;
        return ValueTask.FromResult(new TLBytes(bytes, 0, bytes.Length));
    }

    private static byte[] Build()
    {
        Vector countries = new();
        foreach ((string iso2, string name, List<(string Code, string Pattern)> codes) in ReadCountries())
        {
            Vector countryCodes = new();
            foreach ((string code, string pattern) in codes)
            {
                var builder = CountryCode.Builder().CountryCodeProperty(Encoding.UTF8.GetBytes(code));
                if (pattern.Length > 0)
                {
                    VectorOfString patterns = new();
                    patterns.AppendTLBytes(Encoding.UTF8.GetBytes(pattern));
                    builder.Patterns(patterns);
                }
                using var countryCode = builder.Build();
                countryCodes.AppendTLObject(countryCode.ToReadOnlySpan());
            }

            using var country = Country.Builder()
                .Iso2(Encoding.UTF8.GetBytes(iso2))
                .DefaultName(Encoding.UTF8.GetBytes(name))
                .CountryCodes(countryCodes)
                .Build();
            countries.AppendTLObject(country.ToReadOnlySpan());
        }

        ReadOnlySpan<byte> payload = countries.ToReadOnlySpan();
        _hash = payload.Length * 31 + countries.Constructor;
        var result = CountriesList.Builder()
            .Countries(countries)
            .Hash(_hash)
            .Build();
        return result.TLBytes!.Value.AsSpan().ToArray();
    }

    private static List<(string Iso2, string Name, List<(string, string)> Codes)> ReadCountries()
    {
        var order = new List<(string Iso2, string Name, List<(string, string)> Codes)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        using Stream stream = typeof(GetCountriesListHandler).Assembly
            .GetManifestResourceStream("Ferrite.Services.Help.countries.txt")
            ?? throw new InvalidOperationException("countries.txt is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] parts = line.Split(';');
            if (parts.Length < 3) continue;
            string code = parts[0].Trim();
            string iso2 = parts[1].Trim();
            string name = parts[2].Trim();
            string pattern = parts.Length > 3 ? parts[3].Trim() : "";
            if (code.Length == 0 || iso2.Length == 0 || name.Length == 0) continue;

            if (!index.TryGetValue(iso2, out int position))
            {
                index[iso2] = order.Count;
                order.Add((iso2, name, new List<(string, string)>()));
                position = order.Count - 1;
            }
            order[position].Codes.Add((code, pattern));
        }

        return order;
    }
}
