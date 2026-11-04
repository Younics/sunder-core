using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.App.Bridge;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Avalonia.Hosting;

internal sealed class AppChannel(IAppServiceChannelV1 channel)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static JsonElement Encode<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    internal static T Decode<T>(JsonElement value) => value.Deserialize<T>(Json)!;
    internal T Get<T>(string operation, object? args = null)
    {
        try { return Decode<T>(channel.Get(operation, Encode(args))); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async ValueTask<T> Call<T>(string operation, object? args, CancellationToken token)
    {
        try { return Decode<T>(await channel.InvokeAsync(operation, Encode(args), token).ConfigureAwait(false)); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async ValueTask Call(string operation, object? args, CancellationToken token)
    {
        try { await channel.InvokeAsync(operation, Encode(args), token).ConfigureAwait(false); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async ValueTask<AppChannel> Open(string operation, object? args, CancellationToken token)
    {
        try { return new(await channel.OpenAsync(operation, Encode(args), token).ConfigureAwait(false)); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async ValueTask<Stream?> Read(string operation, object? args, CancellationToken token)
    {
        try
        {
            var stream = await channel.OpenReadAsync(operation, Encode(args), token).ConfigureAwait(false);
            return stream is null ? null : new AppBridgeStream(stream, error => error is AppServiceExceptionV1 bridge ? Translate(bridge) : null);
        }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async ValueTask<T> Write<T>(string operation, object? args, Stream content, CancellationToken token)
    {
        using var input = new AppBridgeStream(content, error => error is SunderRpcException rpc
            ? new AppServiceExceptionV1("rpc", Encode(rpc.Error), rpc.Message) : null, leaveOpen: true);
        try { return Decode<T>(await channel.WriteAsync(operation, Encode(args), input, token).ConfigureAwait(false)); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
    }

    internal async IAsyncEnumerable<T> Watch<T>(string operation, object? args, [EnumeratorCancellation] CancellationToken token)
    {
        IAsyncEnumerator<JsonElement> items;
        try { items = channel.WatchAsync(operation, Encode(args), token).GetAsyncEnumerator(token); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
        try
        {
            while (true)
            {
                JsonElement current;
                try
                {
                    if (!await items.MoveNextAsync().ConfigureAwait(false)) break;
                    current = items.Current;
                }
                catch (AppServiceExceptionV1 error) { throw Translate(error); }
                yield return Decode<T>(current);
            }
        }
        finally
        {
            try { await items.DisposeAsync().ConfigureAwait(false); }
            catch (AppServiceExceptionV1 error) { throw Translate(error); }
        }
    }

    internal async ValueTask DisposeAsync()
    {
        try { await channel.DisposeAsync().ConfigureAwait(false); }
        catch (AppServiceExceptionV1 error) { throw Translate(error); }
        catch (AggregateException error) { throw TranslateDisposal(error); }
    }
    private static Exception TranslateDisposal(Exception error) => error switch
    {
        AppServiceExceptionV1 bridge => Translate(bridge),
        AggregateException aggregate => new AggregateException(aggregate.InnerExceptions.Select(TranslateDisposal)),
        _ => error,
    };
    private static Exception Translate(AppServiceExceptionV1 error) => error.Domain == "rpc"
        ? new SunderRpcException(Decode<SunderRpcError>(error.Error), error.HostAuthenticated)
        : new InvalidOperationException(error.Message);
}
