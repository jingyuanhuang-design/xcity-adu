using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.StaticFiles;
using XBuildApi.Lookup;
using XBuildApi.Lookup.Providers;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddProvider(new SimpleFileLoggerProvider(Path.Combine(Directory.GetCurrentDirectory(), "logs")));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHttpClient("overpass", c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "XBuildApi/1.0");
});
builder.Services.AddHttpClient("overpass-fast", c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "XBuildApi/1.0");
    c.Timeout = TimeSpan.FromSeconds(4);
}).ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    ConnectTimeout = TimeSpan.FromSeconds(2)
});
builder.Services.AddHttpClient("tiles", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("xhomes.ai/1.0");
});
builder.Services.AddHttpClient("arcgis", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("xhomes.ai/1.0");
    c.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var proxyUrl =
        builder.Configuration["Http:Proxy"]
        ?? builder.Configuration["Proxy:Url"]
        ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
        ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
        ?? Environment.GetEnvironmentVariable("ALL_PROXY");

    if (!string.IsNullOrWhiteSpace(proxyUrl) && !proxyUrl.Contains("://", StringComparison.OrdinalIgnoreCase))
        proxyUrl = "http://" + proxyUrl.Trim();

    var handler = new System.Net.Http.SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    };

    if (!string.IsNullOrWhiteSpace(proxyUrl))
    {
        handler.Proxy = new System.Net.WebProxy(proxyUrl);
        handler.UseProxy = true;
    }

    return handler;
});
builder.Services.AddHttpClient("ai", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("xhomes.ai/1.0");
    c.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddControllers();

builder.Services.AddSingleton<OsmBuildingsService>();
builder.Services.AddSingleton<AiAduPlanner>();
builder.Services.AddSingleton<PlanBuilder>();
builder.Services.AddSingleton<SeattleOfficialLookupProvider>();
builder.Services.AddSingleton<NewYorkOfficialLookupProvider>();
builder.Services.AddSingleton<NewJerseyOfficialLookupProvider>();
builder.Services.AddSingleton<XBuildApi.Lookup.Providers.KingCountyLookupProvider>();

builder.Services.AddSingleton<XBuildApi.Site.OverpassRoadService>();
// King County city-specific adapters registered before the Seattle/WA catch-all so they take priority.
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.BellevueRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.RedmondRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.KirklandRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.RentonRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.KingCountyRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.SeattleRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.NewYorkRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.ISiteRegionAdapter, XBuildApi.Site.NewJerseyRegionAdapter>();
builder.Services.AddSingleton<XBuildApi.Site.SiteRegionResolver>();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
var staticContentTypes = new FileExtensionContentTypeProvider();
staticContentTypes.Mappings[".glb"] = "model/gltf-binary";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticContentTypes });

app.Use(async (ctx, next) =>
{
    var sw = Stopwatch.StartNew();
    var qs = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : "";
    app.Logger.LogInformation("收到请求 method={Method} path={Path} query={Query}", ctx.Request.Method, ctx.Request.Path.Value ?? "", qs ?? "");
    try
    {
        await next();
    }
    finally
    {
        sw.Stop();
        app.Logger.LogInformation("返回响应 status={StatusCode} elapsedMs={ElapsedMs} path={Path}", ctx.Response.StatusCode, sw.ElapsedMilliseconds, ctx.Request.Path.Value ?? "");
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = app.Urls.Count > 0 ? string.Join(", ", app.Urls) : "(no urls)";
    app.Logger.LogInformation("服务已启动 urls={Urls}", urls);
    app.Logger.LogInformation("后端启动成功");
});

app.MapControllers();

app.Run();

sealed class SimpleFileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, SimpleFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SimpleFileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, c => new SimpleFileLogger(_dir, c, _lock));
    }

    public void Dispose()
    {
    }
}

sealed class SimpleFileLogger : ILogger
{
    private readonly string _dir;
    private readonly string _category;
    private readonly SemaphoreSlim _lock;

    public SimpleFileLogger(string dir, string category, SemaphoreSlim @lock)
    {
        _dir = dir;
        _category = category;
        _lock = @lock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(msg) && exception is null) return;

        static string LevelToZh(LogLevel level) => level switch
        {
            LogLevel.Trace => "跟踪",
            LogLevel.Debug => "调试",
            LogLevel.Information => "信息",
            LogLevel.Warning => "警告",
            LogLevel.Error => "错误",
            LogLevel.Critical => "严重",
            _ => "未知"
        };

        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var line = $"{ts} [{LevelToZh(logLevel)}] {_category}: {msg}";
        if (exception != null) line += $"{Environment.NewLine}{exception}";
        line += Environment.NewLine;

        _lock.Wait();
        try
        {
            var file = Path.Combine(_dir, $"xbuildapi-{DateTimeOffset.Now:yyyyMMdd}.log");
            File.AppendAllText(file, line, System.Text.Encoding.UTF8);
        }
        finally
        {
            _lock.Release();
        }
    }
}

static class LogText
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.Length <= maxLen) return s;
        return s.Substring(0, maxLen);
    }

    public static string Json(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, _jsonOptions);
        }
        catch
        {
            return value?.ToString() ?? "";
        }
    }
}

static class LookupAiParcelInfoHelper
{
    private sealed record Upstream(
        string Provider,
        string BaseUrl,
        string Model,
        string? ApiKey,
        string AuthHeader,
        string? AuthScheme);

    private sealed record AiParsed(LookupAiParcelInfo Info, string? EligibilityNextState);

    public static async Task<(LookupAiParcelInfo Info, string? EligibilityNextState)> TryGetAsync(
        IConfiguration cfg,
        IHttpClientFactory httpFactory,
        string address,
        string city,
        string state,
        JsonObject parcel,
        JsonObject buildings,
        LookupPlan plan,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        static int GetExistingUnitsCountFromPlan(LookupPlan p)
        {
            var n = p.StructuresCount ?? p.Structures?.Count ?? 0;
            return Math.Max(0, n);
        }

        var existingUnitsCount = GetExistingUnitsCountFromPlan(plan);
        var fallback = BuildFallback(plan, existingUnitsCount);

        var upstream = ResolveUpstream(cfg);
        if (upstream is null)
            return (fallback, null);

        var addressHintForLog = RedactAddressHint(address, city, state);
        var addressHintForAi = RedactAddressHint(address, city, state);

        try
        {
            var sideSetbackFt = TryGetMeasureDistanceFt(plan, "side-left") ?? TryGetMeasureDistanceFt(plan, "side-right");
            var rearSetbackFt = TryGetMeasureDistanceFt(plan, "rear");

            logger.LogInformation(
                "AI 地块信息查询请求 provider={Provider} model={Model} addressHint={AddressHint}",
                upstream.Provider,
                upstream.Model,
                addressHintForLog);

            var http = httpFactory.CreateClient("ai");
            using var req = new HttpRequestMessage(HttpMethod.Post, upstream.BaseUrl);
            req.Headers.Accept.ParseAdd("application/json");
            ApplyAuth(req, upstream);

            var input = new JsonObject
            {
                ["addressHint"] = addressHintForAi,
                ["city"] = city,
                ["state"] = state,
                ["computed"] = new JsonObject
                {
                    ["lotAreaSqft"] = ComputeLotAreaSqft(plan.Lot.Polygon),
                    ["lotWidthFt"] = plan.Lot.WidthFt,
                    ["lotDepthFt"] = plan.Lot.HeightFt,
                    ["existingUnits"] = existingUnitsCount,
                    ["sideSetbackFt"] = sideSetbackFt,
                    ["rearSetbackFt"] = rearSetbackFt
                },
                ["parcelProperties"] = parcel["properties"]?.DeepClone()
            };

            var externalSources = await TryFetchExternalSources(httpFactory, city, state, parcel, cancellationToken, logger);
            if (externalSources is not null && externalSources.Count > 0)
            {
                input["externalSources"] = externalSources;
                var zoningFromSources = TryExtractZoningFromExternalSources(externalSources);
                if (!string.IsNullOrWhiteSpace(zoningFromSources))
                {
                    input["computed"]!["zoningFromExternalSources"] = zoningFromSources;
                    logger.LogInformation("ExternalSources 已提取 zoningFromExternalSources={Zoning}", zoningFromSources);
                }
            }

            logger.LogInformation(
                "AI 地块信息输入 input={Input}",
                LogText.Truncate(input.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }), 8000));

            var sys =
                "你是一个美国住宅地块信息解析助手。只允许使用输入 JSON 中提供的内容（包括 externalSources），不允许自行联网查询。\n" +
                "请输出以下 9 个字段：前 7 个字段都要给出中英文双语（zh/en）；zoningIsLikely 为布尔值；eligibilityNextState 为字符串枚举。\n" +
                "若输入里没有足够信息：除非可以基于 externalSources/parcelProperties/computed 做出“近似推断”，否则返回 zh=\"未知\"、en=\"Unknown\"；eligibilityNextState 返回 needs-review。\n" +
                "若 computed.zoningFromExternalSources 有值：视为明确 zoning 信息来源（无需推断），zoningIsLikely=false。\n" +
                "允许的近似推断：仅限 zoning/land use 相关内容，可依据 externalSources 或 parcelProperties 中与 zoning/land use 明确相关的字段/文本进行推断；若使用了推断：zoning 里仍然输出“推断后的最佳结论（不要附加任何推断/Estimated/Likely 字样）”，并把 zoningIsLikely=true，同时把 eligibilityNextState 设置为 needs-review（除非 externalSources 明确给出住宅/非住宅结论）。\n" +
                "若不是推断：zoningIsLikely=false。\n" +
                "字段：zoning, zoningIsLikely, lotArea, lotDimensions, heightLimit, existingUnits, utilityAccess, setbacks, eligibilityNextState。\n" +
                "existingUnits 表示该地块“已存在建筑物数量”（例如主屋、车库、棚子等），应优先参考 computed.existingUnits；不是“可居住单元数”。\n" +
                "lotDimensions 英文格式要求类似：41'W x 130' D（宽x深）。\n" +
                "setbacks 请用类似：Front 10 ft / Rear 20 ft / Side 5 ft 的表达；若缺少某项则用 Unknown。\n" +
                "externalSources（如有）是从政府/官方站点抓取的网页节选，可作为可靠来源；只允许引用其中明确出现的内容。\n" +
                "eligibilityNextState 仅允许返回以下三种之一：ineligible / residence-type / needs-review。\n" +
                "判断规则：\n" +
                "- 若 zoning/land use 清晰表明不是住宅用地（例如 Commercial/Industrial/Office 等），返回 ineligible。\n" +
                "- 若清晰表明是住宅用地（例如 Residential，或常见住宅分区如 R1/R2/R3/RH/RL 等），返回 residence-type。\n" +
                "- 若无法从输入中确定（未知、缺失、矛盾），返回 needs-review。\n" +
                "只输出 JSON，不要输出任何其他文本。\n" +
                "输出 JSON 结构示例：{\"zoning\":{\"zh\":\"...\",\"en\":\"...\"},\"zoningIsLikely\":false,\"lotArea\":{\"zh\":\"...\",\"en\":\"...\"},\"lotDimensions\":{\"zh\":\"...\",\"en\":\"...\"},\"heightLimit\":{\"zh\":\"...\",\"en\":\"...\"},\"existingUnits\":{\"zh\":\"...\",\"en\":\"...\"},\"utilityAccess\":{\"zh\":\"...\",\"en\":\"...\"},\"setbacks\":{\"zh\":\"...\",\"en\":\"...\"},\"eligibilityNextState\":\"needs-review\"}";

            var payload = new JsonObject
            {
                ["model"] = upstream.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = sys },
                    new JsonObject { ["role"] = "user", ["content"] = input.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) }
                },
                ["temperature"] = 0
            };

            if (upstream.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase))
                payload["response_format"] = new JsonObject { ["type"] = "json_object" };

            req.Content = new StringContent(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), System.Text.Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var respText = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation(
                "AI 地块信息查询响应 status={StatusCode} provider={Provider} model={Model} len={Len}",
                (int)resp.StatusCode,
                upstream.Provider,
                upstream.Model,
                respText.Length);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "AI 地块信息查询失败 status={StatusCode} provider={Provider} model={Model} body={Body}",
                    (int)resp.StatusCode,
                    upstream.Provider,
                    upstream.Model,
                    LogText.Truncate(respText, 2000));
                return (fallback, null);
            }

            var parsed = TryParseAiResult(respText);
            if (parsed is null)
            {
                logger.LogWarning(
                    "AI 地块信息解析失败 provider={Provider} model={Model} body={Body}",
                    upstream.Provider,
                    upstream.Model,
                    LogText.Truncate(respText, 2000));
                return (fallback, null);
            }

            logger.LogInformation(
                "AI 地块信息查询成功 provider={Provider} model={Model} eligibilityNextState={NextState} zoningEn={ZoningEn}",
                upstream.Provider,
                upstream.Model,
                parsed.EligibilityNextState ?? "null",
                LogText.Truncate(parsed.Info.Zoning?.En ?? "", 120));

            return (MergeWithFallback(parsed.Info, fallback), parsed.EligibilityNextState);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI 地块信息查询异常 addressHint={AddressHint}", addressHintForLog);
            return (fallback, null);
        }
    }

    public static LookupAiParcelInfo BuildFallbackOnly(LookupPlan plan)
    {
        var existingUnitsCount = Math.Max(0, plan.StructuresCount ?? plan.Structures?.Count ?? 0);
        return BuildFallback(plan, existingUnitsCount);
    }

    private static string RedactAddressHint(string address, string city, string state)
    {
        var a = (address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(a))
            return $"{city}, {state}".Trim().Trim(',');

        a = System.Text.RegularExpressions.Regex.Replace(a, @"^\s*\d+\s+", "");
        a = System.Text.RegularExpressions.Regex.Replace(a, @"\s+(Apt|Unit|#)\s*\w+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        a = System.Text.RegularExpressions.Regex.Replace(a, @"\b\d{5}(?:-\d{4})?\b", "");
        a = System.Text.RegularExpressions.Regex.Replace(a, @"\s+,", ",").Trim().Trim(',');

        if (!string.IsNullOrWhiteSpace(city) && !a.Contains(city, StringComparison.OrdinalIgnoreCase))
            a = $"{a}, {city}";
        if (!string.IsNullOrWhiteSpace(state) && !a.Contains(state, StringComparison.OrdinalIgnoreCase))
            a = $"{a}, {state}";

        return a.Trim().Trim(',');
    }

    private static string NormalizeAddressHint(string address, string city, string state)
    {
        var a = (address ?? "").Trim();
        if (string.IsNullOrWhiteSpace(a))
            return $"{city}, {state}".Trim().Trim(',');

        a = System.Text.RegularExpressions.Regex.Replace(a, @"\s+,", ",").Trim().Trim(',');

        if (!string.IsNullOrWhiteSpace(city) && !a.Contains(city, StringComparison.OrdinalIgnoreCase))
            a = $"{a}, {city}";
        if (!string.IsNullOrWhiteSpace(state) && !a.Contains(state, StringComparison.OrdinalIgnoreCase))
            a = $"{a}, {state}";

        return a.Trim().Trim(',');
    }

    private static LookupAiParcelInfo BuildFallback(LookupPlan plan, int existingUnitsCount)
    {
        static LookupBilingualText Unknown() => new() { Zh = "未知", En = "Unknown" };

        var areaSqft = ComputeLotAreaSqft(plan.Lot.Polygon);
        var w = plan.Lot.WidthFt;
        var d = plan.Lot.HeightFt;
        var dimsEn = $"{FmtFt(w)}'W x {FmtFt(d)}' D";
        var dimsZh = $"{FmtFt(w)}英尺宽 x {FmtFt(d)}英尺深";

        var sideSetbackFt = TryGetMeasureDistanceFt(plan, "side-left") ?? TryGetMeasureDistanceFt(plan, "side-right");
        var rearSetbackFt = TryGetMeasureDistanceFt(plan, "rear");
        var sbEn = $"Front Unknown / Rear {(rearSetbackFt.HasValue ? $"{FmtFt(rearSetbackFt.Value)} ft" : "Unknown")} / Side {(sideSetbackFt.HasValue ? $"{FmtFt(sideSetbackFt.Value)} ft" : "Unknown")}";
        var sbZh = $"前院 未知 / 后院 {(rearSetbackFt.HasValue ? $"{FmtFt(rearSetbackFt.Value)} 英尺" : "未知")} / 侧院 {(sideSetbackFt.HasValue ? $"{FmtFt(sideSetbackFt.Value)} 英尺" : "未知")}";

        return new LookupAiParcelInfo
        {
            Zoning = Unknown(),
            ZoningIsLikely = false,
            LotArea = new LookupBilingualText { Zh = $"{Math.Round(areaSqft)} 平方英尺", En = $"{Math.Round(areaSqft)} sq ft" },
            LotDimensions = new LookupBilingualText { Zh = dimsZh, En = dimsEn },
            HeightLimit = Unknown(),
            ExistingUnits = new LookupBilingualText { Zh = $"已有 {existingUnitsCount} 栋建筑", En = $"{existingUnitsCount} existing building(s)" },
            UtilityAccess = Unknown(),
            Setbacks = new LookupBilingualText { Zh = sbZh, En = sbEn }
        };
    }

    private static LookupAiParcelInfo MergeWithFallback(LookupAiParcelInfo parsed, LookupAiParcelInfo fallback)
    {
        static LookupBilingualText Pick(LookupBilingualText? v, LookupBilingualText fb)
        {
            if (v is null) return fb;
            var zh = (v.Zh ?? "").Trim();
            var en = (v.En ?? "").Trim();
            if (string.IsNullOrWhiteSpace(zh) || string.IsNullOrWhiteSpace(en)) return fb;
            return new LookupBilingualText { Zh = zh, En = en };
        }

        static int? TryParseExistingUnitsCount(LookupBilingualText? v)
        {
            if (v is null) return null;
            var s = $"{v.En} {v.Zh}";
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+");
            if (!m.Success) return null;
            if (int.TryParse(m.Value, out var n)) return n;
            return null;
        }

        var zoning = Pick(parsed.Zoning, fallback.Zoning);
        var zoningIsFallback =
            string.Equals((zoning.Zh ?? "").Trim(), (fallback.Zoning.Zh ?? "").Trim(), StringComparison.Ordinal)
            && string.Equals((zoning.En ?? "").Trim(), (fallback.Zoning.En ?? "").Trim(), StringComparison.Ordinal);

        var existingUnits = Pick(parsed.ExistingUnits, fallback.ExistingUnits);
        var euParsed = TryParseExistingUnitsCount(existingUnits);
        var euFallback = TryParseExistingUnitsCount(fallback.ExistingUnits);
        if (euParsed.HasValue && euFallback.HasValue && euParsed.Value < euFallback.Value)
            existingUnits = fallback.ExistingUnits;

        return new LookupAiParcelInfo
        {
            Zoning = zoning,
            ZoningIsLikely = !zoningIsFallback && parsed.ZoningIsLikely,
            LotArea = Pick(parsed.LotArea, fallback.LotArea),
            LotDimensions = Pick(parsed.LotDimensions, fallback.LotDimensions),
            HeightLimit = Pick(parsed.HeightLimit, fallback.HeightLimit),
            ExistingUnits = existingUnits,
            UtilityAccess = Pick(parsed.UtilityAccess, fallback.UtilityAccess),
            Setbacks = Pick(parsed.Setbacks, fallback.Setbacks)
        };
    }

    private static AiParsed? TryParseAiResult(string respText)
    {
        try
        {
            var content = ExtractContent(respText);
            if (string.IsNullOrWhiteSpace(content)) return null;
            if (JsonNode.Parse(content) is not JsonObject obj) return null;

            var eligibilityNextState = (obj["eligibilityNextState"]?.GetValue<string>() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(eligibilityNextState)) eligibilityNextState = null;

            var zoningIsLikely = false;
            try
            {
                zoningIsLikely = obj["zoningIsLikely"]?.GetValue<bool>() ?? false;
            }
            catch
            {
                zoningIsLikely = false;
            }

            var zoning = ParseBilingual(obj["zoning"]);
            var inferredByText = LooksLikeInferredZoning(zoning);
            if (obj["zoningIsLikely"] is null && inferredByText) zoningIsLikely = true;
            if (zoningIsLikely || inferredByText) zoning = StripInferredMarkersFromZoning(zoning);

            return new AiParsed(
                new LookupAiParcelInfo
                {
                    Zoning = zoning,
                    ZoningIsLikely = zoningIsLikely,
                    LotArea = ParseBilingual(obj["lotArea"]),
                    LotDimensions = ParseBilingual(obj["lotDimensions"]),
                    HeightLimit = ParseBilingual(obj["heightLimit"]),
                    ExistingUnits = ParseBilingual(obj["existingUnits"]),
                    UtilityAccess = ParseBilingual(obj["utilityAccess"]),
                    Setbacks = ParseBilingual(obj["setbacks"])
                },
                eligibilityNextState);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeInferredZoning(LookupBilingualText zoning)
    {
        var zh = (zoning.Zh ?? "").Trim();
        var en = (zoning.En ?? "").Trim();

        if (zh.Contains("推断", StringComparison.OrdinalIgnoreCase)) return true;
        if (zh.Contains("估计", StringComparison.OrdinalIgnoreCase)) return true;
        if (zh.Contains("可能", StringComparison.OrdinalIgnoreCase)) return true;

        if (en.Contains("estimated", StringComparison.OrdinalIgnoreCase)) return true;
        if (en.Contains("likely", StringComparison.OrdinalIgnoreCase)) return true;
        if (en.Contains("inferred", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static LookupBilingualText StripInferredMarkersFromZoning(LookupBilingualText zoning)
    {
        static string CleanEn(string s)
        {
            s = (s ?? "").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\(\s*(likely|estimated|inferred)\s*\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*estimated\s+to\s+be\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*(estimated|inferred)\s*[:：]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[-–—]\s*(likely|estimated|inferred)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return s;
        }

        static string CleanZh(string s)
        {
            s = (s ?? "").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[（(]\s*推断\s*[）)]\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*推断\s*[:：]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^\s*估计\s*[:：]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*[-–—]\s*可能\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*（?\s*可能\s*）?\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            return s;
        }

        return new LookupBilingualText
        {
            Zh = CleanZh(zoning.Zh),
            En = CleanEn(zoning.En)
        };
    }

    private static LookupBilingualText ParseBilingual(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var zh = (obj["zh"]?.GetValue<string>() ?? "").Trim();
            var en = (obj["en"]?.GetValue<string>() ?? "").Trim();
            return new LookupBilingualText { Zh = zh, En = en };
        }

        var s = (node?.GetValue<string>() ?? "").Trim();
        return new LookupBilingualText { Zh = s, En = s };
    }

    private static string? ExtractContent(string respText)
    {
        try
        {
            var root = JsonNode.Parse(respText) as JsonObject;
            return root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? root?["choices"]?[0]?["text"]?.GetValue<string>()
                   ?? root?["reply"]?.GetValue<string>()
                   ?? root?["output"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonArray?> TryFetchExternalSources(
        IHttpClientFactory httpFactory,
        string city,
        string state,
        JsonObject parcel,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        try
        {
            city = (city ?? "").Trim();
            state = (state ?? "").Trim().ToUpperInvariant();

            var allowed = state switch
            {
                "WA" => new List<string> { "gisdata.kingcounty.gov", "gisdata.seattle.gov", "services.arcgis.com", "blue.kingcounty.com" },
                "CA" => new List<string> { "arcgis.gis.lacounty.gov", "services8.arcgis.com" },
                "NY" => new List<string> { "gisservices.its.ny.gov" },
                "OR" => new List<string> { "services8.arcgis.com" },
                "MA" => new List<string> { "arcgisserver.digital.mass.gov" },
                "NJ" => new List<string> { "maps.nj.gov" },
                _ => new List<string>()
            };

            var urls = new List<string>();
            var c = GeoJsonUtils.TryGetFeatureCentroidLonLat(parcel);
            if (!c.HasValue) return null;
            var lon = c.Value.lon;
            var lat = c.Value.lat;

            if (state == "WA")
            {
                var pin = TryGetPinFromParcel(parcel);
                if (!string.IsNullOrWhiteSpace(pin))
                {
                    var where = Uri.EscapeDataString($"PIN='{pin}'");
                    urls.Add($"https://gisdata.kingcounty.gov/arcgis/rest/services/OpenDataPortal/property__parcel_area/FeatureServer/439/query?f=json&where={where}&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1");
                    urls.Add($"https://gisdata.seattle.gov/server/rest/services/COS/COS_PlanningAndLandUse/MapServer/5/query?f=json&where={where}&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1");
                    urls.Add($"https://blue.kingcounty.com/Assessor/eRealProperty/Detail.aspx?ParcelNbr={Uri.EscapeDataString(pin)}");
                }

                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://gisdata.kingcounty.gov/arcgis/rest/services/OpenDataPortal/property__parcel_area/FeatureServer/439/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://gisdata.seattle.gov/server/rest/services/COS/COS_PlanningAndLandUse/MapServer/5/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=ZONING&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));

                if (city.Equals("SEATTLE", StringComparison.OrdinalIgnoreCase))
                {
                    urls.Add(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "https://services.arcgis.com/ZOyb2t4B0UYuYNYH/arcgis/rest/services/Current_Land_Use_Zoning_Detail_2/FeatureServer/0/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=ZONING,DETAIL_DESC&returnGeometry=false&outSR=4326&resultRecordCount=1",
                        lon,
                        lat));
                }
            }
            else if (state == "CA")
            {
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://arcgis.gis.lacounty.gov/arcgis/rest/services/DRP/Open_Data_Old/MapServer/3/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://services8.arcgis.com/Xr1lDrwMv89PhjD9/arcgis/rest/services/California_Statewide_Zoning_North/FeatureServer/1/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://services8.arcgis.com/Xr1lDrwMv89PhjD9/arcgis/rest/services/California_Statewide_Zoning_South/FeatureServer/0/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
            }
            else if (state == "NY")
            {
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://gisservices.its.ny.gov/arcgis/rest/services/NYS_Tax_Parcels_Public/FeatureServer/1/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
            }
            else if (state == "OR")
            {
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://services8.arcgis.com/8PAo5HGmvRMlF2eU/arcgis/rest/services/Zoning/FeatureServer/0/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
            }
            else if (state == "MA")
            {
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://arcgisserver.digital.mass.gov/arcgisserver/rest/services/AGOL/MassGIS_Address_Point_and_Address_Validation/MapServer/2/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
            }
            else if (state == "NJ")
            {
                urls.Add(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "https://maps.nj.gov/arcgis/rest/services/Framework/Cadastral/MapServer/0/query?f=json&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=false&outSR=4326&resultRecordCount=1",
                    lon,
                    lat));
            }

            if (urls.Count == 0) return null;

            var arr = new JsonArray();
            var http = httpFactory.CreateClient("arcgis");

            for (var i = 0; i < urls.Count; i++)
            {
                var raw = urls[i];
                var url = raw;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) continue;

                if (allowed.Count > 0)
                {
                    var host = (uri.Host ?? "").Trim().ToLowerInvariant();
                    var ok = false;
                    for (var j = 0; j < allowed.Count; j++)
                    {
                        var rule = allowed[j].Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(rule)) continue;
                        if (rule.StartsWith(".") && host.EndsWith(rule, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                        if (host.Equals(rule, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
                    }
                    if (!ok) continue;
                }

                using var perReqCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                perReqCts.CancelAfter(TimeSpan.FromSeconds(10));

                using var resp = await http.GetAsync(uri, perReqCts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    logger.LogInformation("ExternalSources 响应 status={StatusCode} url={Url}", (int)resp.StatusCode, uri.ToString());
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync(perReqCts.Token);
                logger.LogInformation("ExternalSources 响应 status={StatusCode} len={Len} url={Url}", (int)resp.StatusCode, text?.Length ?? 0, uri.ToString());
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (IsArcGisErrorJson(text))
                {
                    var msg = TryGetArcGisErrorMessage(text);
                    logger.LogInformation("ExternalSources ArcGIS error url={Url} message={Message}", uri.ToString(), msg ?? "Unknown");
                    continue;
                }

                var cleaned = SimplifyExternalSourceContent(text, uri);
                cleaned = (cleaned ?? "").Trim();
                var maxLen = string.Equals(uri.Host, "blue.kingcounty.com", StringComparison.OrdinalIgnoreCase) ? 200000 : 30000;
                if (cleaned.Length > maxLen) cleaned = cleaned.Substring(0, maxLen);
                if (string.IsNullOrWhiteSpace(cleaned)) continue;

                logger.LogInformation(
                    "ExternalSources 清洗后 url={Url} cleanedLen={Len} snippet={Snippet}",
                    uri.ToString(),
                    cleaned.Length,
                    cleaned);

                arr.Add(new JsonObject
                {
                    ["url"] = uri.ToString(),
                    ["content"] = cleaned
                });
            }

            if (arr.Count == 0) return null;
            return arr;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ExternalSources 抓取失败 city={City} state={State}", city, state);
            return null;
        }
    }

    private static string SimplifyExternalSourceContent(string? s, Uri sourceUri)
    {
        var trimmed = (s ?? "").TrimStart();
        if (!string.IsNullOrWhiteSpace(trimmed) && (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)))
        {
            if (IsArcGisErrorJson(trimmed)) return "";
            var simplified = TrySimplifyArcGisJson(trimmed);
            if (!string.IsNullOrWhiteSpace(simplified)) return simplified;
        }

        if (string.Equals(sourceUri.Host, "blue.kingcounty.com", StringComparison.OrdinalIgnoreCase))
            return ExtractKingCountyBlueAssessorContent(s ?? "");

        return StripHtml(s ?? "", false);
    }

    private static bool IsArcGisErrorJson(string jsonText)
    {
        try
        {
            if (JsonNode.Parse(jsonText) is not JsonObject root) return false;
            return root.ContainsKey("error");
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetArcGisErrorMessage(string jsonText)
    {
        try
        {
            if (JsonNode.Parse(jsonText) is not JsonObject root) return null;
            if (root["error"] is not JsonObject err) return null;
            var code = err["code"]?.ToString();
            var msg = (err["message"]?.ToString() ?? "").Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(msg)) return $"{code}: {msg}";
            if (!string.IsNullOrWhiteSpace(msg)) return msg;
            if (!string.IsNullOrWhiteSpace(code)) return code.Trim().Trim('"');
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TrySimplifyArcGisJson(string jsonText)
    {
        try
        {
            if (JsonNode.Parse(jsonText) is not JsonObject root) return null;
            if (root.ContainsKey("error")) return null;
            if (root["features"] is not JsonArray feats || feats.Count == 0) return null;
            if (feats[0] is not JsonObject f0) return null;
            if (f0["attributes"] is not JsonObject attrs) return null;

            bool KeepKey(string k)
            {
                if (string.IsNullOrWhiteSpace(k)) return false;
                var key = k.Trim();
                if (key.Contains("objectid", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("globalid", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("shape", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("apn", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("account", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("uuid", StringComparison.OrdinalIgnoreCase)) return false;
                if (key.Contains("id", StringComparison.OrdinalIgnoreCase) && !key.Contains("zoning", StringComparison.OrdinalIgnoreCase) && !key.Contains("zone", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (key.Equals("MAJOR", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Equals("MINOR", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Contains("pin", StringComparison.OrdinalIgnoreCase)) return true;

                if (key.Contains("zoning", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Contains("zone", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Contains("land", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Contains("use", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Equals("LUT", StringComparison.OrdinalIgnoreCase)) return true;

                return false;
            }

            var picked = new JsonObject();
            foreach (var kv in attrs)
            {
                if (!KeepKey(kv.Key)) continue;
                picked[kv.Key] = kv.Value?.DeepClone();
            }

            if (picked.Count == 0) return null;
            return picked.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractZoningFromExternalSources(JsonArray externalSources)
    {
        for (var i = 0; i < externalSources.Count; i++)
        {
            if (externalSources[i] is not JsonObject o) continue;
            var content = (o["content"]?.GetValue<string>() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (content.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(content) is JsonObject obj)
                    {
                        foreach (var k in new[] { "ZONING", "CURRZONE", "CURR_ZONING", "ZONE", "ZONENAME", "ZONE_NAME" })
                        {
                            if (obj[k] is null) continue;
                            var z = (obj[k]?.ToString() ?? "").Trim().Trim('"');
                            if (!string.IsNullOrWhiteSpace(z)) return z;
                        }

                        foreach (var kv in obj)
                        {
                            var key = (kv.Key ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(key)) continue;
                            if (key.Contains("zoning", StringComparison.OrdinalIgnoreCase) || key.Contains("currzone", StringComparison.OrdinalIgnoreCase) || key.Contains("zone", StringComparison.OrdinalIgnoreCase))
                            {
                                var z = (kv.Value?.ToString() ?? "").Trim().Trim('"');
                                if (!string.IsNullOrWhiteSpace(z)) return z;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            var m = System.Text.RegularExpressions.Regex.Match(content, "\"ZONING\"\\s*:\\s*\"(?<z>[^\"]+)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var z = (m.Groups["z"]?.Value ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(z)) return z;
            }

            m = System.Text.RegularExpressions.Regex.Match(
                content,
                "\\bZONING\\b\\s*[:=]\\s*(?<z>[A-Za-z0-9._/-]+(?:\\s*\\([^\\)]+\\))?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var z = (m.Groups["z"]?.Value ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(z)) return z;
            }
        }

        return null;
    }

    private static string? TryGetPinFromParcel(JsonObject parcel)
    {
        try
        {
            if (parcel["properties"] is not JsonObject props) return null;
            string? ReadString(JsonNode? n)
            {
                if (n is null) return null;
                try { return n.GetValue<string>(); } catch { }
                try { return n.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { }
                try { return n.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture); } catch { }
                return n.ToString();
            }

            var pin = (ReadString(props["PIN"]) ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pin)) return pin;

            if (props["fields"] is JsonObject fields)
            {
                foreach (var k in new[] { "pin", "PIN", "parcelnumb", "apn", "ll_uuid" })
                {
                    var v = (ReadString(fields[k]) ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (k.Equals("ll_uuid", StringComparison.OrdinalIgnoreCase) && v.Contains("kc:", StringComparison.OrdinalIgnoreCase))
                        v = v.Replace("kc:", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtml(string s, bool keepScriptsAndStyles)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var withoutScripts = keepScriptsAndStyles
            ? s
            : System.Text.RegularExpressions.Regex.Replace(s, "<script[\\s\\S]*?</script>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var withoutStyles = keepScriptsAndStyles
            ? withoutScripts
            : System.Text.RegularExpressions.Regex.Replace(withoutScripts, "<style[\\s\\S]*?</style>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var withoutTags = System.Text.RegularExpressions.Regex.Replace(withoutStyles, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        var collapsed = System.Text.RegularExpressions.Regex.Replace(decoded ?? "", "\\s+", " ");
        return collapsed;
    }

    private static string ExtractKingCountyBlueAssessorContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        string? ExtractTd(string label)
        {
            var pattern = $"(?is)<t[dh][^>]*>\\s*{System.Text.RegularExpressions.Regex.Escape(label)}\\s*</t[dh]>\\s*<t[dh][^>]*>(?<v>[\\s\\S]*?)</t[dh]>";
            var m = System.Text.RegularExpressions.Regex.Match(html, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = (m.Groups["v"]?.Value ?? "").Trim();
            v = StripHtml(v, true);
            v = (v ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (v.Length > 200) v = v.Substring(0, 200);
            return v;
        }

        string? ExtractLoose(string label)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                html,
                $"(?is)\\b{System.Text.RegularExpressions.Regex.Escape(label)}\\b[\\s\\S]{{0,200}}?(?<v>[A-Za-z0-9._/-]+(?:\\s*\\([^\\)]+\\))?)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var v = (m.Groups["v"]?.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(v)) return null;
            if (v.Length > 200) v = v.Substring(0, 200);
            return v;
        }

        var zoning =
            ExtractTd("Zoning")
            ?? ExtractTd("ZONING")
            ?? ExtractLoose("Zoning")
            ?? ExtractLoose("ZONING");

        var landUse =
            ExtractTd("Land Use")
            ?? ExtractTd("LAND USE")
            ?? ExtractLoose("Land Use")
            ?? ExtractLoose("LAND USE");

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(zoning)) sb.Append("ZONING: ").Append(zoning).Append('\n');
        if (!string.IsNullOrWhiteSpace(landUse)) sb.Append("LAND_USE: ").Append(landUse).Append('\n');

        var text = HtmlToStructuredText(html);
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString().Trim();
    }

    private static string HtmlToStructuredText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Replace("\r\n", "\n").Replace("\r", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<script[\\s\\S]*?</script>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<style[\\s\\S]*?</style>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "</(p|div|li|tr|h1|h2|h3|h4|h5|h6)>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<(td|th)[^>]*>", "\t", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "</(td|th)>", "\t", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<li[^>]*>", "- ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);

        var lines = s.Split('\n');
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line is null) continue;
            line = System.Text.RegularExpressions.Regex.Replace(line, "[ \\t\\u00A0]+", " ").Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length < 2) continue;
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }

    private static int GetBuildingsCount(JsonObject fc)
    {
        if (fc["features"] is JsonArray arr) return arr.Count;
        return 0;
    }

    private static double? TryGetMeasureDistanceFt(LookupPlan plan, string kind)
    {
        var lines = plan.MeasureLines;
        if (lines is null) return null;
        for (var i = 0; i < lines.Count; i++)
        {
            var k = (lines[i]?.Kind ?? "").Trim();
            if (k.Equals(kind, StringComparison.OrdinalIgnoreCase))
            {
                var d = lines[i].DistanceFt;
                if (double.IsFinite(d) && d > 0) return d;
            }
        }
        return null;
    }

    private static double ComputeLotAreaSqft(List<FtPoint> polygon)
    {
        if (polygon.Count < 3) return 0;
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += (a.XFt * b.YFt) - (b.XFt * a.YFt);
        }
        return Math.Abs(sum) * 0.5;
    }

    private static string FmtFt(double ft)
    {
        var v = Math.Round(ft, 1);
        if (Math.Abs(v - Math.Round(v)) < 0.0001) return Math.Round(v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Upstream? ResolveUpstream(IConfiguration cfg)
    {
        var defaultProvider = (cfg["AI:DefaultProvider"] ?? "").Trim();
        var list = new List<Upstream>();
        foreach (var child in cfg.GetSection("AI:Upstreams").GetChildren())
        {
            var provider = (child["Provider"] ?? "").Trim();
            var baseUrl = (child["BaseUrl"] ?? "").Trim();
            var model = (child["Model"] ?? "").Trim();
            var authHeader = (child["AuthHeader"] ?? "Authorization").Trim();
            var authScheme = (child["AuthScheme"] ?? "Bearer").Trim();
            var apiKey = child["ApiKey"];

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(baseUrl)) continue;
            if (string.IsNullOrWhiteSpace(model)) model = "gpt-4o-mini";
            if (string.IsNullOrWhiteSpace(authHeader)) authHeader = "Authorization";
            if (string.IsNullOrWhiteSpace(authScheme)) authScheme = null;

            list.Add(new Upstream(provider, baseUrl, model, apiKey, authHeader, authScheme));
        }

        if (list.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(defaultProvider))
        {
            if (list.Count == 1) return list[0];
            return null;
        }

        return list.FirstOrDefault(u => u.Provider.Equals(defaultProvider, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyAuth(HttpRequestMessage req, Upstream upstream)
    {
        if (string.IsNullOrWhiteSpace(upstream.ApiKey)) return;

        if (upstream.AuthHeader.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = string.IsNullOrWhiteSpace(upstream.AuthScheme) ? "Bearer" : upstream.AuthScheme;
            req.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {upstream.ApiKey}");
            return;
        }

        var val = string.IsNullOrWhiteSpace(upstream.AuthScheme)
            ? upstream.ApiKey
            : $"{upstream.AuthScheme} {upstream.ApiKey}";
        req.Headers.TryAddWithoutValidation(upstream.AuthHeader, val);
    }
}
