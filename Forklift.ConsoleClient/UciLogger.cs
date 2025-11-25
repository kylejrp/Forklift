using System.Threading.Channels;
using OneOf;
using static Forklift.Core.Search;

static class UciLogger
{
    private static readonly Channel<OneOf<string, SearchInfo>> _channel =
        Channel.CreateUnbounded<OneOf<string, SearchInfo>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly Task _writerTask = Task.Run(async () =>
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            line.Switch(
                str => Console.WriteLine(str),
                info => Console.WriteLine(info)
            );
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

    public readonly record struct SearchInfo(
        SearchSummary Summary,
        TimeSpan Elapsed
    )
    {
        public override string ToString()
        {
            return $"info depth {Summary.CompletedDepth} score cp {Summary.BestScore} nodes {Summary.NodesSearched} nps {Summary.NodesSearched / Math.Max(Elapsed.TotalMilliseconds / 1000.0, 1):F0} time {Elapsed.TotalMilliseconds:F0}{$" pv {Summary.BestMove?.ToUCIString() ?? "0000"}"}";
        }
    };

    public static bool TryLog(OneOf<string, SearchInfo> line)
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
