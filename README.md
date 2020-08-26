# C# events as asynchronous streams with ReactiveX or Channels

This is the source repro my article published on [DEV.TO](https://dev.to/noseratio/c-events-as-asynchronous-streams-with-reactivex-or-channels-82k).

As I am getting myself up to date with the modern C# language features, I'd like to share what feels like an interesting and natural use case for [`IAsyncEnumerable`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.iasyncenumerable-1?view=dotnet-plat-ext-3.1): **iterating through a sequence of arbitrary events**.

### A (not so) brief introduction <small>(TLDR, [skip](#skipTo))</small>

Support for asynchronous iterators (or ["async streams"](https://docs.microsoft.com/en-us/dotnet/csharp/tutorials/generate-consume-asynchronous-stream)) has been added to C# 8.0, and includes the following language and runtime features:

 - [`IAsyncEnumerable`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.iasyncenumerable-1?view=dotnet-plat-ext-3.1), [`IAsyncEnumerator`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.generic.iasyncenumerator-1?view=dotnet-plat-ext-3.1), [`IAsyncDisposable`](https://docs.microsoft.com/en-us/dotnet/api/system.iasyncdisposable?view=dotnet-plat-ext-3.1) 
 - [`await foreach`](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/foreach-in)  
 - [`await using`](https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/using-statement)
 - [`EnumeratorCancellation`](https://docs.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.enumeratorcancellationattribute?view=dotnet-plat-ext-3.1)

Asynchronous streams may come handy when implementing various producer/consumer scenarios in C#. `IAsyncEnumerable` and `await foreach` are just async counterparts for `IEnumerable` and `foreach`. Same as with `IEnumerable<T> EnumSomething()` or `async Task<T> DoSomethingAsync()`, when the C# compiler encounters `async IAsyncEnumerable<T> EnumSomethingAsync()`, it generates a special state machine class. The compiler breaks the logical execution flow within `EnumSomethingAsync` into multiple asynchronous continuations, separated by `await` or `yield return` operators. Prior to C# 8.0, it wasn't possible to combine these two within the same method. Now it is, and the whole set of the familiar `Linq` extension is now available as part of [System.Linq.Async](https://www.nuget.org/packages/System.Linq.Async), to operate on the asynchronous stream of data generated via `yield return` or by other means, like below. 

There is an abundance of great material on the web to help familiarize yourself with the concept of asynchronous streams. I could highly recommend ["Iterating with Async Enumerables in C# 8"](https://docs.microsoft.com/en-us/archive/msdn-magazine/2019/november/csharp-iterating-with-async-enumerables-in-csharp-8), by [Stephen Toub](https://devblogs.microsoft.com/dotnet/author/toub).<a name="skipTo"></a>

What I'd like to show here is **how to turn a sequence of events of any kind of origin into an iterable async stream**. While there are many ways of doing this, I'd like to focus on the following two: with **Reactive Extensions** ([`System.Reactive`](https://github.com/dotnet/reactive)) or by using an unbound [`Channel`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.channels.channel-1?view=netcore-3.1) from the relatively new **[`System.Threading.Channels`](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels)**.
<a name="reactivex"></a>
### Producing async streams with Reactive Extensions

To illustrate this, I wrote a simple WinForms app ([source gist](https://gist.github.com/noseratio/6143efc9ad2311b423551df4ccae69b5)) that has two independent event sources: a timer and a button. It's a contrived example but it's easy to play with and step through, to show the concept. 

We turn timer ticks and button clicks into Reactive's [`IObservable`](https://docs.microsoft.com/en-us/dotnet/api/system.iobservable-1?view=netcore-3.1) observables with [`Observable.FromEventPattern`](https://docs.microsoft.com/en-us/previous-versions/dotnet/reactive-extensions/hh229705(v%3Dvs.103)). Then we combine two observables into one using [`Observable.Merge`](https://docs.microsoft.com/en-us/previous-versions/dotnet/reactive-extensions/hh211658(v=vs.103)):

```c#
// observe Click events
var clickObservable = Observable
    .FromEventPattern(
        handler => button.Click += handler,
        handler => button.Click -= handler)
    .Select(_ => (button as Component, $"Clicked on {DateTime.Now}"));

// observe Tick events
var tickObservable = Observable
    .FromEventPattern(
        handler => timer.Tick += handler,
        handler => timer.Tick -= handler)
    .Select(_ => (timer as Component, $"Ticked on {DateTime.Now}"));

// merge two observables
var mergedObservable = Observable.Merge(clickObservable, tickObservable);
``` 

Now we simply turn the combined observable into an instance of `IAsyncEnumerable` with `ToAsyncEnumerable()`, and we can asynchronously iterate though all events with `await foreach` as they occur:

```c#
// process events as async stream via ToAsyncEnumerable(),
// that's when the actual subscriptions happen, i.e.,  
// the event handlers get connected to their corresponding events
await ReadEventStreamAsync(mergedObservable.ToAsyncEnumerable(), cts.Token);
```

```c#
static async Task ReadEventStreamAsync(
    IAsyncEnumerable<(Component, string)> source, 
    CancellationToken token)
{
    await foreach (var (component, text) in source.WithCancellation(token))
    {
        // e.g., delay processing
        await Task.Delay(100, token);
        Console.WriteLine($"{component.GetType().Name}: {text}");
    }
}
```

Various LINQ operators can now be applied to the `source` stream above, like projection, filtering, etc.

Running it and clicking the button:

![A simple Winforms app](https://github.com/noseratio/AsyncEvents/raw/main/async-events.png)<a name="channels"></a>

### Producing async streams with `System.Threading.Channels`

What if we don't want to involve Reactive Extensions here? They do seem a bit like an overkill for a simple producer/consumer workflow like above. 

No worries, we can use an unbound [`Channel`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.channels.channel?view=netcore-3.1) to act as buffer for our event stream. A `Channel` is like a pipe, we can push event data objects into one side of the pipe, and fetch them asynchronously from the other. In case with an unbound channel, its internal buffer size is limited by the available memory. In real-life scenarios, we'd almost always want to limit that. Channels are just one way of implementing the Asynchronous Queue data structure in .NET, [there are some others](https://stackoverflow.com/a/21225922/1768303), notably Dataflow [`BufferBlock<T>`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.dataflow.bufferblock-1?view=netcore-3.1). For more details on Channels, visit "[An Introduction to System.Threading.Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/)" by Stephen Toub.
 
So, we introduce a helper class `EventChannel` ([source](https://github.com/noseratio/AsyncEvents/blob/main/EventChannel.cs)) to expose [`Channel.Writer.TryWrite`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.channels.channelwriter-1.trywrite?view=netcore-3.1), [`Channel.Reader.ReadAllAsync`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.channels.channelreader-1.readallasync?view=netcore-3.1) and manage the scope of event handlers as `IDisposable`:

```c#
public class EventChannel<T> : IDisposable
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();

    public Task Completion => _channel.Reader.Completion;

    /// <summary>Queue an event item to the write side of the channel</summary>
    public bool Post(T item)
    {
        return _channel.Writer.TryWrite(item);
    }

    /// <summary>Read queued events as an async stream</summary>
    public IAsyncEnumerable<T> ToAsyncEnumerable(CancellationToken token)
    {
        return _channel.Reader.ReadAllAsync(token);
    }

    /// <summary>A simple helper to wrap even handler scope as IDisposable</summary>
    internal struct EventSubscription<TEventHandler> : IDisposable
        where TEventHandler : Delegate
    {
        private readonly Action _unsubscribe;

        public EventSubscription(
            TEventHandler handler,
            Action<TEventHandler> subscribe,
            Action<TEventHandler> unsubscribe)
        {
            subscribe(handler);
            _unsubscribe = () => unsubscribe(handler);
        }

        public void Dispose()
        {
            _unsubscribe();
        }
    }

    /// <summary>
    /// Subscribe to an event
    /// </summary>
    public IDisposable Subscribe<TEventHandler>(
        TEventHandler handler,
        Action<TEventHandler> subscribe,
        Action<TEventHandler> unsubscribe) where TEventHandler : Delegate
    {
        return new EventSubscription<TEventHandler>(handler, subscribe, unsubscribe);
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
    }
}
```
The event subscription code now looks like this:

```c#
using var eventChannel = new EventChannel<(Component, string)>();

// push Click events to the channel
using var clickHandler = eventChannel.Subscribe<EventHandler>(
    (s, e) => eventChannel.Post((button as Component, $"Cicked on {DateTime.Now}")),
    handler => button!.Click += handler,
    handler => button!.Click -= handler);

// push Tick events to the channel
using var tickHandler = eventChannel.Subscribe<EventHandler>(
    (s, e) => eventChannel.Post((timer as Component, $"Ticked on {DateTime.Now}")),
    handler => timer!.Tick += handler,
    handler => timer!.Tick -= handler);

// process events as async stream via ToAsyncEnumerable(),
await ReadEventStreamAsync(eventChannel.ToAsyncEnumerable(cts.Token), cts.Token);
```
The consumer part almost hasn't changed, we only removed `source.WithCancellation(token)` which is now redundant:

```c#
static async Task ReadEventStreamAsync(
    IAsyncEnumerable<(Component, string)> source, 
    CancellationToken token)
{
    await foreach (var (component, text) in source)
    {
        // e.g., delay processing
        await Task.Delay(100, token);
        Console.WriteLine($"{component.GetType().Name}: {text}");
    }
}
```
It produces exactly the same result as with ReactiveX (provided we can manage to click the button with the same intervals 🙂). The full source code (a .NET Core 3.1 project) can be found [in this repo](https://github.com/noseratio/AsyncEvents).

### Conclusion

The domain of the problems that C# asynchronous streams can help solving certainly overlaps with that of the Reactive Extensions (aka ReactiveX/Rx.NET/Rx). E.g., in the first example above I could have just [subscribed](https://docs.microsoft.com/en-us/previous-versions/dotnet/reactive-extensions/ff402852(v=vs.103)) to `mergedObservable` notifications and used the powerful toolbox of `System.Reactive.Linq` extensions to process them.

That said, I personally find it is easier to understand the pseudo-liner code flow of `async`/`await`, than the fluent syntax of ReactiveX. In my opinion, it may also be easier to structure the exception handling (*particularly*, [cancellations](https://docs.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads)) while using the familiar language constructs like `foreach`, `yield return`, `try`/`catch`/`finally`. I myself only recently came across `IAsyncEnumerable`, and I decided to try it out by [implementing coroutines in C#](https://dev.to/noseratio/asynchronous-coroutines-with-c-8-0-and-iasyncenumerable-2e04). I certainly didn't need ReactiveX for that. 

However, it should be quite possible to combine ReactiveX and C# asynchronous streams to work together for complex asynchronous workflows.

I hope this has been useful. I'll be blogging more on this topic as I make progress with my [side project](https://github.com/postprintum/devcomrade).

**Updated**, here's a follow-up blog post:
[Asynchronous coroutines with C# 8.0 and IAsyncEnumerable](https://dev.to/noseratio/asynchronous-coroutines-with-c-8-0-and-iasyncenumerable-2e04).

[Follow me on twitter](https://twitter.com/noseratio) for further updates, if interested.
 