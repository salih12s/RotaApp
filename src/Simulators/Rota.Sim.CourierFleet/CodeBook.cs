using System.Text.Json;

namespace Rota.Sim.CourierFleet;

/// <summary>
/// The delivery codes the merchant simulator handed over, read from a JSON-lines file (<c>{"orderId":"…","code":"4821"}</c> per line).
/// In real life the merchant tells the customer and the customer tells the courier; here a file stands in for that chain, so the
/// two simulators stay separate programs. The file is only ever appended to, so reading while the other side writes is safe.
/// </summary>
public sealed class CodeBook(string path)
{
    private readonly Dictionary<Guid, string> _codes = [];
    private readonly Lock _gate = new();

    /// <summary>Parses one line; <c>false</c> for a half-written or foreign line (the writer may be in the middle of it).</summary>
    public static bool TryParseLine(string line, out Guid orderId, out string code)
    {
        orderId = default;
        code = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("orderId", out var id) && id.TryGetGuid(out orderId)
                && root.TryGetProperty("code", out var value) && value.GetString() is { Length: > 0 } text)
            {
                code = text;
                return true;
            }
        }
        catch (JsonException)
        {
            // A line that is not complete yet.
        }

        return false;
    }

    /// <summary>The code for the order, waiting up to <paramref name="patience"/> for the merchant side to write it; <c>null</c> if it never shows up.</summary>
    public async Task<string?> WaitForAsync(Guid orderId, TimeSpan patience, CancellationToken stop)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(patience);
        try
        {
            while (true)
            {
                Refresh();
                lock (_gate)
                {
                    if (_codes.TryGetValue(orderId, out var code))
                    {
                        return code;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), limit.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void Refresh()
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        lock (_gate)
        {
            foreach (var line in lines)
            {
                if (TryParseLine(line, out var orderId, out var code))
                {
                    _codes[orderId] = code;
                }
            }
        }
    }
}
