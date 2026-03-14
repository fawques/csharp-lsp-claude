using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

var warmupTimeout = TimeSpan.FromSeconds(3);
var responseQueue = new BlockingCollection<JsonDocument>();
var writeLock = new object();
var initializeDone = false;
var firstRealRequestDone = false;

var proc = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "csharp-ls",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = false,
        UseShellExecute = false,
    }
};
proc.Start();

var clientIn = Console.OpenStandardInput();
var clientOut = Console.OpenStandardOutput();

// Read responses from csharp-ls in background
var readerThread = new Thread(() =>
{
    try
    {
        while (ReadMessage(proc.StandardOutput.BaseStream) is { } msg)
        {
            if (msg.RootElement.TryGetProperty("id", out _) &&
                !msg.RootElement.TryGetProperty("method", out _))
            {
                responseQueue.Add(msg);
            }
            else
            {
                lock (writeLock)
                {
                    WriteMessage(clientOut, msg);
                }
            }
        }
    }
    catch { }
    finally
    {
        responseQueue.CompleteAdding();
    }
}) { IsBackground = true };
readerThread.Start();

try
{
    while (ReadMessage(clientIn) is { } msg)
    {
        var hasId = msg.RootElement.TryGetProperty("id", out var idProp);
        var hasMethod = msg.RootElement.TryGetProperty("method", out var methodProp);
        var isRequest = hasId && hasMethod;
        var isNotification = !hasId && hasMethod;

        WriteMessage(proc.StandardInput.BaseStream, msg);

        if (isNotification || !isRequest)
            continue;

        var method = methodProp.GetString();

        // Always let initialize through normally — protocol requires a real response
        if (method == "initialize")
        {
            var response = WaitForResponse();
            if (response is null) break;
            lock (writeLock) { WriteMessage(clientOut, response); }
            initializeDone = true;
            continue;
        }

        // First non-initialize request after warmup: race against timeout
        if (initializeDone && !firstRealRequestDone)
        {
            firstRealRequestDone = true;
            if (responseQueue.TryTake(out var response, warmupTimeout))
            {
                lock (writeLock) { WriteMessage(clientOut, response); }
            }
            else
            {
                var empty = JsonDocument.Parse(
                    $$"""{"jsonrpc":"2.0","id":{{idProp.GetRawText()}},"result":null}""");
                lock (writeLock) { WriteMessage(clientOut, empty); }
                // Drain the real response in background
                _ = Task.Run(() => responseQueue.TryTake(out _, TimeSpan.FromMinutes(2)));
            }
            continue;
        }

        // All subsequent requests: proxy normally
        {
            var response = WaitForResponse();
            if (response is null) break;
            lock (writeLock) { WriteMessage(clientOut, response); }
        }
    }
}
catch (Exception) when (proc.HasExited) { }
finally
{
    if (!proc.HasExited)
    {
        proc.Kill();
        proc.WaitForExit(5000);
    }
}

JsonDocument? WaitForResponse()
{
    return responseQueue.TryTake(out var r, TimeSpan.FromMinutes(2)) ? r : null;
}

static JsonDocument? ReadMessage(Stream stream)
{
    var headerBuilder = new StringBuilder();
    while (true)
    {
        var b = stream.ReadByte();
        if (b == -1) return null;
        headerBuilder.Append((char)b);
        if (headerBuilder.Length >= 4 && headerBuilder.ToString().EndsWith("\r\n\r\n"))
            break;
    }

    var contentLength = 0;
    foreach (var line in headerBuilder.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            contentLength = int.Parse(line.Split(':')[1].Trim());
    }

    if (contentLength == 0) return null;

    var body = new byte[contentLength];
    var read = 0;
    while (read < contentLength)
    {
        var n = stream.Read(body, read, contentLength - read);
        if (n == 0) return null;
        read += n;
    }

    return JsonDocument.Parse(body);
}

static void WriteMessage(Stream stream, JsonDocument doc)
{
    var body = Encoding.UTF8.GetBytes(doc.RootElement.GetRawText());
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    stream.Write(header);
    stream.Write(body);
    stream.Flush();
}
