using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LocalTranslate;

public sealed record TranslationResult(string Text, string? Detected);
public sealed record Language(string Code, string EnglishName);

public sealed class TranslationException(string message) : Exception(message);

/// <summary>Thin client for the LibreTranslate HTTP API with a small in-memory cache.</summary>
public sealed class Translator
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly Dictionary<string, TranslationResult> cache = new();
    readonly Queue<string> order = new();
    const int CacheLimit = 500;

    public List<Language> Languages { get; private set; } = new();

    static string Key(string text, string target) => target + "\u0000" + text;

    public bool TryGetCached(string text, string target, out TranslationResult result)
    {
        lock (cache) return cache.TryGetValue(Key(text, target), out result!);
    }

    // LibreTranslate translates a few requests at once; queueing beyond that only adds latency.
    readonly SemaphoreSlim gate = new(4);
    readonly Dictionary<string, Task<TranslationResult>> inFlight = new();

    public Task<TranslationResult> TranslateAsync(string text, Settings s)
    {
        var target = s.Target;
        if (TryGetCached(text, target, out var hit)) return Task.FromResult(hit);

        // The same text asked twice (hover + screen mode, repeated labels) shares one request.
        var key = Key(text, target);
        lock (inFlight)
        {
            if (inFlight.TryGetValue(key, out var running)) return running;
            var task = TranslateUncachedAsync(text, s);
            inFlight[key] = task;
            task.ContinueWith(_ => { lock (inFlight) inFlight.Remove(key); }, TaskScheduler.Default);
            return task;
        }
    }

    async Task<TranslationResult> TranslateUncachedAsync(string text, Settings s)
    {
        await gate.WaitAsync();
        try { return await RequestAsync(text, s); }
        finally { gate.Release(); }
    }

    async Task<TranslationResult> RequestAsync(string text, Settings s)
    {
        var target = s.Target;
        var payload = new Dictionary<string, string>
        {
            ["q"] = text, ["source"] = "auto", ["target"] = target, ["format"] = "text"
        };
        if (!string.IsNullOrWhiteSpace(s.ApiKey)) payload["api_key"] = s.ApiKey.Trim();

        HttpResponseMessage res;
        try
        {
            res = await http.PostAsync(Url(s, "/translate"),
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        }
        catch (HttpRequestException)
        {
            throw new TranslationException(Loc.T("error.unreachable", s.Server));
        }
        catch (TaskCanceledException) // HttpClient reports its timeout this way
        {
            throw new TranslationException(Loc.T("error.timeout"));
        }

        var body = await res.Content.ReadAsStringAsync();
        string? error = null, translated = null, detected = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e)) error = e.GetString();
            if (root.TryGetProperty("translatedText", out var t)) translated = t.GetString();
            if (root.TryGetProperty("detectedLanguage", out var d) && d.ValueKind == JsonValueKind.Object &&
                d.TryGetProperty("language", out var dl)) detected = dl.GetString();
        }
        catch (JsonException) { error ??= Loc.T("error.unexpected", (int)res.StatusCode); }

        if (!res.IsSuccessStatusCode || error != null || translated == null)
            throw new TranslationException(error ?? $"HTTP {(int)res.StatusCode}");

        var result = new TranslationResult(translated, detected);
        lock (cache)
        {
            var key = Key(text, target);
            if (cache.TryAdd(key, result)) order.Enqueue(key);
            while (order.Count > CacheLimit) cache.Remove(order.Dequeue());
        }
        return result;
    }

    public async Task<List<Language>> LoadLanguagesAsync(Settings s)
    {
        var json = await http.GetStringAsync(Url(s, "/languages"));
        using var doc = JsonDocument.Parse(json);
        Languages = doc.RootElement.EnumerateArray()
            .Select(l => new Language(l.GetProperty("code").GetString()!, l.GetProperty("name").GetString() ?? ""))
            .ToList();
        return Languages;
    }

    public async Task<bool> PingAsync(Settings s)
    {
        try { await LoadLanguagesAsync(s); return true; }
        catch { return false; }
    }

    public void ClearCache()
    {
        lock (cache) { cache.Clear(); order.Clear(); }
    }

    /// <summary>Language name in the UI language (see <see cref="Loc"/>).</summary>
    public static string DisplayName(string code, string? serverEnglishName = null) => Loc.LanguageName(code, serverEnglishName);

    /// <summary>The server's languages with their display names, sorted by name in the UI language.</summary>
    public List<(string Code, string Name)> NamedLanguages() =>
        Languages.Select(l => (l.Code, Name: Loc.LanguageName(l.Code, l.EnglishName)))
                 .OrderBy(l => l.Name, StringComparer.Create(Loc.Culture, true))
                 .ToList();

    static string Url(Settings s, string path) => s.Server.TrimEnd('/') + path;

    public static bool SameLanguage(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);
}
