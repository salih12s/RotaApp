using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Rota.Contracts.Tests;

/// <summary>
/// Compares JSON with a snapshot file committed next to the tests. A missing snapshot FAILS (it is
/// never created silently): a new contract must be reviewed by a person and the file added on purpose.
/// The first run writes <c>name.received.json</c> next to it to review and rename.
/// </summary>
public static class SnapshotVerifier
{
    private static readonly JsonSerializerOptions _indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string SnapshotDirectory([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Snapshots");

    public static void Verify(string name, object value)
    {
        var directory = SnapshotDirectory();
        var path = Path.Combine(directory, $"{name}.json");
        var actual = Normalize(JsonSerializer.Serialize(value, _indented));

        if (!File.Exists(path))
        {
            File.WriteAllText(Path.Combine(directory, $"{name}.received.json"), actual);
            throw new Xunit.Sdk.XunitException(
                $"No snapshot for '{name}'. Review {name}.received.json in Snapshots/ and rename it to {name}.json if it is the intended contract.");
        }

        var expected = Normalize(File.ReadAllText(path));
        actual.ShouldBe(expected, $"The JSON of '{name}' no longer matches its snapshot. If the change is a deliberate new version, add a v2 contract instead of editing v1.");
    }

    // Line endings differ per checkout (CRLF/LF); the contract is the content.
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
