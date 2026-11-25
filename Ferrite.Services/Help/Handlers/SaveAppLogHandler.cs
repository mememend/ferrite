// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2022-2026 Aykut Alparslan KOC

using System.Text;
using Ferrite.TL;
using Ferrite.TL.baseLayer;
using Ferrite.TL.baseLayer.help;

namespace Ferrite.Services.Handlers.HelpMethods;

public sealed class SaveAppLogHandler
{
    [TLFunction(Constructors.baseLayer_SaveAppLog)]
    public ValueTask<TLBytes> Handle(long authKeyId, TLBytes q)
    {
        // exteraGram uploads its own log (including crash reports) with this method;
        // keeping the events in the server log is the only way to see a client trace
        // on a device we cannot attach a debugger to.
        var request = (SaveAppLog)q;
        Vector events = request.Events;
        int count = events.Count;
        for (int i = 0; i < count; i++)
        {
            var view = (InputAppEventView)events.ReadTLObject();
            if (!view.Is(out InputAppEvent item)) continue;
            string data = Encoding.UTF8.GetString(item.Data.ToArray());
            if (data.Length > 8000) data = data[..8000];
            Console.WriteLine($"[applog] time={item.Time} type={Encoding.UTF8.GetString(item.Type.ToArray())} " +
                              $"peer={item.Peer} data={data}");
        }
        var result = BoolTrue.Builder().Build();
        return ValueTask.FromResult(result.TLBytes!.Value);
    }
}
