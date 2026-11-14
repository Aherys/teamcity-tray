using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCityTray;

public sealed class TeamCityClient : IDisposable
{
    private const string BuildFields =
        "build(id,buildTypeId,number,state,status,percentageComplete,webUrl,queuedDate,startDate,finishDate,comment(text)," +
        "triggered(type,user(username),build(id)),buildType(name,projectId,projectName)," +
        "snapshot-dependencies(build(id)),running-info(elapsedSeconds,estimatedTotalSeconds))";

    private const string SeedFields =
        "build(id,buildTypeId,number,state,status,webUrl,queuedDate,startDate,finishDate,comment(text)," +
        "triggered(type,user(username)),buildType(name,projectId,projectName))";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private string _key = "";

    public void Configure(string serverUrl, string token)
    {
        var key = serverUrl + "\n" + token;
        if (key == _key)
        {
            return;
        }
        _key = key;
        _http.BaseAddress = new Uri(serverUrl.Trim().TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetCurrentUserAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("app/rest/users/current", ct);
        return Str(doc.RootElement, "username") ?? throw new InvalidOperationException("No username in response.");
    }

    public async Task<List<BuildInfo>> GetRunningAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            "app/rest/builds?locator=running:true,defaultFilter:false,count:200&fields=" + BuildFields, ct);
        return ParseBuilds(doc.RootElement, false);
    }

    public async Task<List<BuildInfo>> GetQueuedAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("app/rest/buildQueue?fields=" + BuildFields, ct);
        return ParseBuilds(doc.RootElement, true);
    }

    // Recent builds of the current user, finished ones included. Returns an empty list when the server rejects the locator.
    public async Task<List<BuildInfo>> GetRecentMineAsync(string username, DateTime now, CancellationToken ct)
    {
        var since = now.AddHours(-6).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + "%2B0000";
        var locator = $"user:(username:{Uri.EscapeDataString(username)}),defaultFilter:false,sinceDate:{since},count:50";
        try
        {
            using var doc = await GetJsonAsync("app/rest/builds?locator=" + locator + "&fields=" + SeedFields, ct);
            return ParseBuilds(doc.RootElement, false);
        }
        catch (HttpRequestException)
        {
            return new List<BuildInfo>();
        }
    }

    // Queues the same build again as the current user.
    public async Task QueueRerunAsync(long id, string number, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(
            $"app/rest/builds/id:{id}?fields=buildType(id),branchName,comment(text),properties(property(name,value))", ct);
        var root = doc.RootElement;
        var typeId = Obj(root, "buildType") is { } bt ? Str(bt, "id") : null;
        if (string.IsNullOrEmpty(typeId))
        {
            throw new InvalidOperationException("The build has no build type.");
        }

        var comment = "Re-run of #" + number;
        if (Obj(root, "comment") is { } c && Str(c, "text") is { Length: > 0 } original)
        {
            comment += " - " + original;
        }
        var body = new JsonObject
        {
            ["buildType"] = new JsonObject { ["id"] = typeId },
            ["comment"] = new JsonObject { ["text"] = comment },
        };
        if (Str(root, "branchName") is { Length: > 0 } branch)
        {
            body["branchName"] = branch;
        }
        var props = new JsonArray();
        if (Obj(root, "properties") is { } p && p.TryGetProperty("property", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Str(item, "name") is { } name)
                {
                    props.Add(new JsonObject { ["name"] = name, ["value"] = Str(item, "value") ?? "" });
                }
            }
        }
        if (props.Count > 0)
        {
            body["properties"] = new JsonObject { ["property"] = props };
        }

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("app/rest/buildQueue", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }
    }

    // Returns null when the build no longer exists (for example a removed queue entry).
    public async Task<FinalInfo?> GetFinishedAsync(long id, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"app/rest/builds/id:{id}?fields=id,status,state,statusText", ct);
            var root = doc.RootElement;
            return new FinalInfo(Str(root, "state") ?? "", Str(root, "status") ?? "", Str(root, "statusText") ?? "");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static List<BuildInfo> ParseBuilds(JsonElement root, bool queued)
    {
        var result = new List<BuildInfo>();
        if (!root.TryGetProperty("build", out var builds) || builds.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var b in builds.EnumerateArray())
        {
            if (!b.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }
            var buildType = Obj(b, "buildType");
            var triggered = Obj(b, "triggered");
            var deps = new List<long>();
            if (Obj(b, "snapshot-dependencies") is { } snap
                && snap.TryGetProperty("build", out var depBuilds) && depBuilds.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depBuilds.EnumerateArray())
                {
                    if (d.TryGetProperty("id", out var depId) && depId.TryGetInt64(out var depValue))
                    {
                        deps.Add(depValue);
                    }
                }
            }

            var percent = b.TryGetProperty("percentageComplete", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
            int? elapsed = Obj(b, "running-info") is { } info && info.TryGetProperty("elapsedSeconds", out var e) && e.TryGetInt32(out var ev)
                ? ev
                : null;
            string? triggerUser = triggered is { } t && Obj(t, "user") is { } user ? Str(user, "username") : null;
            long? triggerBuild = triggered is { } t2 && Obj(t2, "build") is { } tb && tb.TryGetProperty("id", out var tid) && tid.TryGetInt64(out var tv)
                ? tv
                : null;

            result.Add(new BuildInfo(
                id,
                (buildType is { } bt ? Str(bt, "name") : null) ?? Str(b, "buildTypeId") ?? "?",
                (buildType is { } bt2 ? Str(bt2, "projectId") : null) ?? "",
                (buildType is { } bt3 ? Str(bt3, "projectName") : null) ?? "",
                Str(b, "number") ?? "",
                queued || string.Equals(Str(b, "state"), "queued", StringComparison.OrdinalIgnoreCase),
                Str(b, "webUrl"),
                ParseDate(Str(b, "queuedDate")),
                ParseDate(Str(b, "startDate")),
                triggerUser,
                triggerBuild,
                deps,
                percent,
                elapsed,
                Obj(b, "comment") is { } cm ? Str(cm, "text") : null,
                ParseDate(Str(b, "finishDate")),
                string.Equals(Str(b, "state"), "finished", StringComparison.OrdinalIgnoreCase),
                Str(b, "status")));
        }
        return result;
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // TeamCity dates look like 20250131T120000+0000.
    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 5)
        {
            return null;
        }
        var withColon = s.Insert(s.Length - 2, ":");
        return DateTimeOffset.TryParseExact(withColon, "yyyyMMdd'T'HHmmsszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.UtcDateTime
            : null;
    }

    public void Dispose() => _http.Dispose();
}
