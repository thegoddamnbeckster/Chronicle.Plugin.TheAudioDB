using System.Net;
using System.Text;
using System.Text.Json;
using Chronicle.Plugin.TheAudioDB;
using Chronicle.Plugins.Models;
using Serilog.Core;
using Serilog.Events;

namespace Chronicle.Plugin.TheAudioDB.Tests;

/// <summary>A fake TheAudioDB: answers by the request's path (after the key) and records every request.</summary>
internal sealed class FakeTadb : HttpMessageHandler
{
    public sealed record Reply(HttpStatusCode Status, string Body, TimeSpan? RetryAfter = null);

    private readonly List<(string Path, Func<Reply> Reply)> _routes = [];
    public List<string> Requests { get; } = [];
    public List<string> Urls { get; } = [];
    public int InFlight;
    public int MaxInFlight;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Answers a request whose path (e.g. "search.php?s=Coldplay") equals <paramref name="path"/>.</summary>
    public FakeTadb On(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((path, () => new Reply(status, body)));
        return this;
    }

    public FakeTadb OnSequence(string path, params Reply[] replies)
    {
        var i = 0;
        _routes.Add((path, () => replies[Math.Min(i++, replies.Length - 1)]));
        return this;
    }

    public FakeTadb OnThrow(string path, Exception ex)
    {
        _routes.Add((path, () => throw ex));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = Interlocked.Increment(ref InFlight);
        MaxInFlight = Math.Max(MaxInFlight, current);
        try
        {
            await Task.Delay(5, cancellationToken);
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            // .../api/v1/json/{key}/{path}
            var rest = url[(url.IndexOf("/json/", StringComparison.Ordinal) + 6)..];
            var path = Uri.UnescapeDataString(rest[(rest.IndexOf('/') + 1)..]);
            lock (Requests) Requests.Add(path);

            var route = _routes.FirstOrDefault(r => r.Path == path);
            if (route.Reply is null)
                throw new InvalidOperationException($"Unexpected request: {path}");
            var reply = route.Reply();
            var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") };
            if (reply.RetryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            return response;
        }
        finally { Interlocked.Decrement(ref InFlight); }
    }
}

internal sealed class CapturingSink : ILogEventSink
{
    public List<string> Lines { get; } = [];
    public void Emit(LogEvent logEvent) => Lines.Add(logEvent.RenderMessage() + (logEvent.Exception is null ? "" : " " + logEvent.Exception));
}

internal static class Make
{
    public static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    public static TheAudioDbClient Client(FakeTadb fake, string key = "123", Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? interval = null, Serilog.ILogger? log = null) =>
        new(new HttpClient(fake), key, interval ?? TimeSpan.Zero, log, delay ?? NoDelay);

    public static TheAudioDbMetadataProvider Provider(FakeTadb fake, Settings? settings = null, string key = "123") =>
        new(Client(fake, key), settings ?? Settings.Default);

    public static MediaSearchContext Artist(string name, int? year = null, IReadOnlyDictionary<string, string>? known = null) =>
        new(name, Year: year, HierarchyLevel: 0, MediaTypeName: "music", KnownExternalIds: known, IsRealHierarchyPosition: true);

    public static MediaSearchContext Album(string title, string? artist, int? year = null, IReadOnlyDictionary<string, string>? known = null) =>
        new(title, Year: year, ParentName: artist, HierarchyLevel: 1, MediaTypeName: "music", KnownExternalIds: known, IsRealHierarchyPosition: true);

    public static MediaSearchContext Track(string title, string? album, string? artist, int? number = null, IReadOnlyDictionary<string, string>? known = null) =>
        new(title, ParentName: album, GrandparentName: artist, ItemNumber: number, HierarchyLevel: 2, MediaTypeName: "music",
            KnownExternalIds: known, IsRealHierarchyPosition: true);

    public static Rec Rec(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    public static Rec FirstOf(string fixture, string key) =>
        new(JsonDocument.Parse(FakeTadb.Fixture(fixture)).RootElement.GetProperty(key)[0].Clone());
}
