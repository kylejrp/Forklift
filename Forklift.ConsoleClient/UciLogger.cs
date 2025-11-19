using System.Threading.Channels;

static class UciLogger
{
    private static readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly Task _writerTask = Task.Run(async () =>
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            Console.WriteLine(line);
        }
    });

    static UciLogger()
    {
        _writerTask.ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                Console.Error.WriteLine($"info string exception in UciLogger writer task: {t.Exception.Message.Replace("\r", "").Replace("\n", "\\n")}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static bool TryLog(string line)
    {
        // Non-blocking enqueue
        return _channel.Writer.TryWrite(line);
    }

    public static async Task FlushAndCompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
    }
}
