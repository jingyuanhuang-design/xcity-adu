using System.Text.Json.Nodes;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace XBuildApi.Lookup.Providers;

/// <summary>
/// King County (WA) 通用地址查询 Provider：
/// 先用 King County 官方 ArcGIS Geocoder 定位，再用 King County parcel 图层获取地块 geometry。
/// 建筑轮廓统一使用 OSM（Overpass）。
/// 适用于 Bellevue、Redmond、Kirkland、Renton 等 King County 内非 Seattle 城市。
/// </summary>
public sealed class KingCountyLookupProvider : ILookupProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly OsmBuildingsService _osmBuildings;
    private readonly ILogger<KingCountyLookupProvider> _logger;
    private readonly IConfiguration _cfg;

    public KingCountyLookupProvider(
        IHttpClientFactory httpFactory,
        OsmBuildingsService osmBuildings,
        ILogger<KingCountyLookupProvider> logger,
        IConfiguration cfg)
    {
        _httpFactory = httpFactory;
        _osmBuildings = osmBuildings;
        _logger = logger;
        _cfg = cfg;
    }

    /// <inheritdoc />
    public string ProviderName => "kingcounty-official";

    /// <inheritdoc />
    public int Priority => 90;

    /// <inheritdoc />
    public bool CanHandle(string address, string state)
    {
        if (!string.Equals(state?.Trim(), "WA", StringComparison.OrdinalIgnoreCase)) return false;
        // King County cities (non-Seattle)
        foreach (var city in KingCountyCities)
        {
            if (address.IndexOf(city, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        // King County ZIP codes (excluding Seattle 981xx)
        var zipMatch = System.Text.RegularExpressions.Regex.Match(address, @"\b(980\d{2})\b");
        if (zipMatch.Success) return true;
        return false;
    }

    /// <inheritdoc />
    public async Task<LookupProviderResult?> LookupAsync(string address, string state, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient("arcgis");

        // King County Geocoder → primary; ArcGIS World Geocoder → fallback
        var geocodeUrls = new[]
        {
            "https://gisrevprxy.seattle.gov/arcgis/rest/services/LOCATION_svc/KingCo_Geocoder/GeocodeServer/findAddressCandidates"
            + $"?f=pjson&SingleLine={Uri.EscapeDataString(address)}&maxLocations=5&outSR=4326&outFields=*",
            "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer/findAddressCandidates"
            + $"?f=pjson&SingleLine={Uri.EscapeDataString(address + ", WA")}&maxLocations=5&outSR=4326&outFields=*&countryCode=US"
        };

        JsonObject? cand = null;
        foreach (var geocodeUrl in geocodeUrls)
        {
            _logger.LogInformation("KingCounty 地理编码请求 url={Url}", geocodeUrl);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                using var geoResp = await http.GetAsync(geocodeUrl, timeoutCts.Token);
                if (!geoResp.IsSuccessStatusCode) continue;

                var geoText = await geoResp.Content.ReadAsStringAsync(timeoutCts.Token);
                _logger.LogInformation("KingCounty 地理编码响应 status={StatusCode} len={Len}", (int)geoResp.StatusCode, geoText.Length);

                var geoDoc = JsonNode.Parse(geoText) as JsonObject;
                var candidates = geoDoc?["candidates"] as JsonArray;
                if (candidates is null || candidates.Count == 0) continue;

                JsonObject? best = null;
                foreach (var c in candidates)
                {
                    if (c is not JsonObject co) continue;
                    var score = co["score"]?.GetValue<double>() ?? 0d;
                    if (score < 80) continue;
                    if (best is null || score > (best["score"]?.GetValue<double>() ?? 0d))
                        best = co;
                }

                cand = best ?? (candidates[0] as JsonObject);
                if (cand != null) break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "KingCounty 地理编码请求失败 url={Url}", geocodeUrl);
            }
        }

        if (cand is null) return null;

        var loc = cand["location"] as JsonObject;
        if (loc is null) return null;
        var lon = loc["x"]?.GetValue<double>() ?? double.NaN;
        var lat = loc["y"]?.GetValue<double>() ?? double.NaN;
        if (!double.IsFinite(lon) || !double.IsFinite(lat)) return null;

        var matchedAddr = cand["address"]?.GetValue<string>() ?? address;
        var streetName = LookupUtils.ExtractStreetNameFromSingleLine(matchedAddr);
        var city = ExtractCityFromCandidate(cand) ?? ExtractCityFromAddress(address);
        var st = "WA";

        // Query King County parcel by point
        var parcelUrl =
            "https://gisdata.kingcounty.gov/arcgis/rest/services/OpenDataPortal/property__parcel_area/FeatureServer/439/query"
            + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "?f=geojson&geometryType=esriGeometryPoint&inSR=4326&geometry={0},{1}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=4326&resultRecordCount=1",
                lon, lat);

        _logger.LogInformation("KingCounty parcel 查询 url={Url}", parcelUrl);

        JsonObject? feat0 = null;
        JsonObject? geom = null;
        JsonObject? rawProps = null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var parcelResp = await http.GetAsync(parcelUrl, timeoutCts.Token);
            if (parcelResp.IsSuccessStatusCode)
            {
                var parcelText = await parcelResp.Content.ReadAsStringAsync(timeoutCts.Token);
                _logger.LogInformation("KingCounty parcel 响应 len={Len}", parcelText.Length);
                var parcelDoc = JsonNode.Parse(parcelText) as JsonObject;
                var feats = parcelDoc?["features"] as JsonArray;
                if (feats is { Count: > 0 })
                {
                    feat0 = feats[0] as JsonObject;
                    geom = feat0?["geometry"] as JsonObject;
                    rawProps = feat0?["properties"] as JsonObject ?? new JsonObject();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KingCounty parcel 查询失败");
        }

        if (feat0 is null || geom is null) return null;

        rawProps ??= new JsonObject();
        var pid = ExtractPin(rawProps);

        var fields = new JsonObject
        {
            ["ll_uuid"] = string.IsNullOrWhiteSpace(pid) ? $"kc:{lon:F6},{lat:F6}" : $"kc:{pid}",
            ["parcelnumb"] = pid,
            ["apn"] = pid,
            ["scity"] = string.IsNullOrWhiteSpace(city) ? "King County" : city,
            ["sstate"] = st,
            ["saddstr"] = streetName,
            ["saddsttyp"] = ""
        };

        var props = rawProps.DeepClone() as JsonObject ?? new JsonObject();
        props["fields"] = fields;
        props["formatted_address"] = matchedAddr;

        var parcelFeature = new JsonObject
        {
            ["type"] = "Feature",
            ["geometry"] = geom.DeepClone(),
            ["properties"] = props
        };

        var bbox = GeoJsonUtils.BboxFromPolygon(geom);
        var buildings = await _osmBuildings.QueryAsync(bbox, cancellationToken);

        return new LookupProviderResult
        {
            Provider = ProviderName,
            Parcel = parcelFeature,
            Buildings = buildings,
            StreetName = streetName,
            City = string.IsNullOrWhiteSpace(city) ? "King County" : city,
            State = st,
            Lat = lat,
            Lon = lon
        };
    }

    private static string ExtractPin(JsonObject rawProps)
    {
        try
        {
            var pin = rawProps["PIN"]?.GetValue<string>()
                ?? rawProps["pin"]?.GetValue<string>()
                ?? "";
            if (!string.IsNullOrWhiteSpace(pin)) return pin.Trim();

            var major = rawProps["MAJOR"]?.GetValue<string>() ?? rawProps["major"]?.GetValue<string>() ?? "";
            var minor = rawProps["MINOR"]?.GetValue<string>() ?? rawProps["minor"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(major) || !string.IsNullOrWhiteSpace(minor))
                return $"{major}{minor}".Trim();
        }
        catch { }
        return "";
    }

    private static string? ExtractCityFromCandidate(JsonObject cand)
    {
        try
        {
            if (cand["attributes"] is JsonObject attrs)
            {
                return attrs["City"]?.GetValue<string>()
                    ?? attrs["CITY"]?.GetValue<string>()
                    ?? attrs["city"]?.GetValue<string>();
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractCityFromAddress(string address)
    {
        foreach (var city in KingCountyCities)
        {
            if (address.IndexOf(city, StringComparison.OrdinalIgnoreCase) >= 0)
                return city;
        }
        return null;
    }

    internal static readonly string[] KingCountyCities =
    {
        "Bellevue", "Redmond", "Kirkland", "Renton", "Issaquah",
        "Sammamish", "Mercer Island", "Kenmore", "Bothell", "Shoreline",
        "Burien", "Kent", "Auburn", "Tukwila", "Federal Way",
        "Maple Valley", "Covington", "Black Diamond", "Enumclaw", "Snoqualmie",
        "North Bend", "Carnation", "Duvall", "Woodinville", "Medina",
        "Clyde Hill", "Yarrow Point", "Beaux Arts", "Hunts Point",
        "Newcastle", "Normandy Park", "Des Moines", "SeaTac", "Algona",
        "Pacific", "Milton", "Edgewood"
    };
}
