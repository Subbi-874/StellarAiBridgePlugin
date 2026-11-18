using System;
using System.Collections.Concurrent;
using HarmonyLib;

namespace Stellar.AgyBridge.Core;

public static class InteropResolver
{
    private static readonly ConcurrentDictionary<string, Type?> Cache = new(StringComparer.Ordinal);

    public static Type? FindType(string fullName)
    {
        if (Cache.TryGetValue(fullName, out var cached) && cached != null)
            return cached;

        Type? found = null;
        try
        {
            found = AccessTools.TypeByName(fullName);
        }
        catch { }

        if (found == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    found = asm.GetType(fullName, throwOnError: false);
                    if (found != null) break;
                }
                catch { }
            }
        }

        if (found != null)
            Cache[fullName] = found;

        return found;
    }
}
