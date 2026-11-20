using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Stellar.AgyBridge.Bridge.Storage;

/// <summary>
/// Reads embedded prompt templates, JSON tool schemas, SQL DDL scripts, and native binaries
/// compiled inside <see cref="Stellar.AgyBridge"/> without touching loose disk files.
/// </summary>
public static class EmbeddedAssets
{
    private static readonly Assembly ResourceAssembly = typeof(EmbeddedAssets).Assembly;

    public static string[] ListNames() => ResourceAssembly.GetManifestResourceNames();

    public static byte[] ReadBytes(string logicalName)
    {
        using Stream stream = ResourceAssembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' was not found. Available: {string.Join(", ", ListNames())}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public static string ReadText(string logicalName)
    {
        using Stream stream = ResourceAssembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' was not found. Available: {string.Join(", ", ListNames())}");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string ReadPrompt(string fileName) => ReadText($"Stellar.AgyBridge.Prompts.{fileName}");

    public static string ReadAsset(string fileName) => ReadText($"Stellar.AgyBridge.Bridge.Assets.{fileName}");
}
