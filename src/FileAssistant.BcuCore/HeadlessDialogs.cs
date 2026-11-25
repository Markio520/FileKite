using System;
using System.Collections.Concurrent;

namespace Klocman.Forms.Tools;

// BCU scanners can report recoverable errors from worker threads. Each cleanup host serves one request.
public static class PremadeDialogs
{
    public class NotSendableException(string message) : Exception(message);

    private static readonly ConcurrentQueue<string> warnings = new();
    public static void GenericError(Exception error) => warnings.Enqueue(error.Message);
    public static string[] GetWarnings() => warnings.ToArray();
}
