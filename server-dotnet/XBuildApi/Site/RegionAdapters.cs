using System.Text.Json;
using System.Text.Json.Nodes;
using XBuildApi.Lookup;
using XBuildApi.Lookup.Providers;

namespace XBuildApi.Site;

/// <summary>
/// 地区适配器解析器：从已注册的 <see cref="ISiteRegionAdapter"/> 列表中选出能处理当前地址的 adapter。
/// </summary>
public sealed class SiteRegionResolver
{
    private readonly IReadOnlyList<ISiteRegionAdapter> _adapters;

    /// <summary>
    /// 创建解析器。
    /// </summary>
    /// <param name="adapters">已注册的地区适配器集合。</param>
    public SiteRegionResolver(IEnumerable<ISiteRegionAdapter> adapters)
    {
        _adapters = adapters.ToList();
    }

    /// <summary>
    /// 为地址选择一个可处理的地区适配器。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <returns>匹配到的 adapter；未匹配到则返回 null。</returns>
    public ISiteRegionAdapter? Resolve(string address)
    {
        foreach (var a in _adapters)
        {
            if (a.CanHandle(address)) return a;
        }
        return null;
    }
}

/// <summary>
/// 西雅图（WA）地区适配器：使用 Seattle 官方地址/地块数据源，并组合附近要素（地块/建筑/道路）。
/// </summary>
public sealed class SeattleRegionAdapter : ISiteRegionAdapter
{
    private readonly SeattleOfficialLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<SeattleRegionAdapter> _logger;

    /// <summary>
    /// 创建西雅图地区适配器。
    /// </summary>
    /// <param name="provider">Seattle 官方地块查询 provider。</param>
    /// <param name="osm">OSM 建筑查询服务（Overpass）。</param>
    /// <param name="httpFactory">HttpClient 工厂（需注册 <c>arcgis</c> client）。</param>
    /// <param name="roads">Overpass 道路查询服务。</param>
    /// <param name="planBuilder">统一 plan 计算器（英尺局部坐标系）。</param>
    /// <param name="logger">日志。</param>
    public SeattleRegionAdapter(
        SeattleOfficialLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<SeattleRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    /// <summary>
    /// 地区标识。
    /// </summary>
    public string Name => "seattle";

    /// <summary>
    /// 西雅图地区默认策略参数。
    /// </summary>
    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 20,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    /// <summary>
    /// 仅处理 WA 地址。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <returns>WA 返回 true。</returns>
    public bool CanHandle(string address) => LookupUtils.ExtractState(address) == "WA";

    /// <summary>
    /// 获取目标地块与建筑集合，并计算目标地块 bbox。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>目标数据；找不到地块返回 null。</returns>
    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
    {
        var state = LookupUtils.ExtractState(address);
        var r = await _provider.LookupAsync(address, state, cancellationToken);
        if (r is null) return null;

        var parcel = GeoJsonStd.ParseFeature(r.Parcel.ToJsonString());
        var buildings = GeoJsonStd.ParseFeatureCollection(r.Buildings.ToJsonString());
        var bbox = GeoBbox.FromGeometry(parcel.Geometry);

        return new SiteSubjectData
        {
            Provider = r.Provider,
            City = (r.City ?? "").Trim(),
            State = (r.State ?? state ?? "").Trim().ToUpperInvariant(),
            Lat = r.Lat,
            Lon = r.Lon,
            StreetName = r.StreetName,
            Parcel = parcel,
            Buildings = buildings,
            ParcelBbox = bbox
        };
    }

    /// <summary>
    /// 查询附近地块/建筑/道路。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近要素集合。</returns>
    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var bbox = PadBbox(subject.ParcelBbox, Policy.NearbyPadDegrees);
        var parcels = await QueryNearbyParcelsAsync(bbox, cancellationToken);
        GeoJsonFeatureCollection buildings;
        GeoJsonFeatureCollection roads;
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            buildings = GeoJsonStd.ParseFeatureCollection((await _osm.QueryAsync(bbox, timeoutCts.Token)).ToJsonString());
        }
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            roads = await _roads.QueryRoadsGeoJsonAsync(bbox, timeoutCts.Token);
        }
        return new SiteNearbyData { Parcels = parcels, Buildings = buildings, Roads = roads };
    }

    /// <summary>
    /// 统一 plan 计算（英尺局部坐标系）。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>计算后的 plan。</returns>
    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var parcelJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Parcel)) as JsonObject ?? new JsonObject();
        var buildingsJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Buildings)) as JsonObject ?? new JsonObject();
        var plan = await _planBuilder.BuildAsync(
            parcelJson,
            buildingsJson,
            subject.StreetName,
            Policy.DefaultFrontSetbackFt,
            Policy.DefaultRearSetbackFt,
            Policy.DefaultSideSetbackFt,
            Policy.DefaultHouseSepFt,
            cancellationToken);
        if (plan is null) throw new LookupProviderException(502, "Plan 计算失败");
        return plan;
    }

    /// <summary>
    /// 查询 bbox 内附近地块（KingCounty parcel 图层）。
    /// </summary>
    /// <param name="bboxLonLat">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近地块 FeatureCollection。</returns>
    private async Task<GeoJsonFeatureCollection> QueryNearbyParcelsAsync(double[] bboxLonLat, CancellationToken cancellationToken)
    {
        try
        {
            var minLon = bboxLonLat[0];
            var minLat = bboxLonLat[1];
            var maxLon = bboxLonLat[2];
            var maxLat = bboxLonLat[3];

            var url =
                "https://gisdata.kingcounty.gov/arcgis/rest/services/OpenDataPortal/property__parcel_area/FeatureServer/439/query"
                + $"?f=geojson&geometryType=esriGeometryEnvelope&inSR=4326&geometry={minLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{minLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + "&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=4326&resultRecordCount=200";

            var http = _httpFactory.CreateClient("arcgis");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            _logger.LogInformation("Nearby parcels (KingCounty) 请求 url={Url}", url);
            using var resp = await http.GetAsync(url, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return EmptyFc();
            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(text);
            return GeoJsonStd.ParseFeatureCollection(doc.RootElement);
        }
        catch
        {
            return EmptyFc();
        }
    }

    /// <summary>
    /// 对 bbox 做简单扩展（用于 nearby 查询）。
    /// </summary>
    /// <param name="bbox">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="pad">扩展量（度）。</param>
    /// <returns>扩展后的 bbox。</returns>
    private static double[] PadBbox(double[] bbox, double pad)
    {
        if (bbox.Length < 4) return bbox;
        return new[] { bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad };
    }

    /// <summary>
    /// 空 FeatureCollection。
    /// </summary>
    private static GeoJsonFeatureCollection EmptyFc() => new() { Type = "FeatureCollection", Features = new List<GeoJsonFeature>() };
}

/// <summary>
/// 纽约（NY）地区适配器：使用纽约官方地块 provider，并组合附近要素（地块/建筑/道路）。
/// </summary>
public sealed class NewYorkRegionAdapter : ISiteRegionAdapter
{
    private readonly NewYorkOfficialLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;

    /// <summary>
    /// 创建纽约地区适配器。
    /// </summary>
    /// <param name="provider">NY 官方地块查询 provider。</param>
    /// <param name="osm">OSM 建筑查询服务。</param>
    /// <param name="httpFactory">HttpClient 工厂（需注册 <c>arcgis</c> client）。</param>
    /// <param name="roads">Overpass 道路查询服务。</param>
    /// <param name="planBuilder">统一 plan 计算器。</param>
    public NewYorkRegionAdapter(
        NewYorkOfficialLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
    }

    /// <summary>
    /// 地区标识。
    /// </summary>
    public string Name => "newyork";

    /// <summary>
    /// 纽约地区默认策略参数。
    /// </summary>
    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 20,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    /// <summary>
    /// 仅处理 NY 地址。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <returns>NY 返回 true。</returns>
    public bool CanHandle(string address) => LookupUtils.ExtractState(address) == "NY";

    /// <summary>
    /// 获取目标地块与建筑集合，并计算 bbox。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>目标数据；找不到地块返回 null。</returns>
    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
    {
        var state = LookupUtils.ExtractState(address);
        var r = await _provider.LookupAsync(address, state, cancellationToken);
        if (r is null) return null;
        var parcel = GeoJsonStd.ParseFeature(r.Parcel.ToJsonString());
        var buildings = GeoJsonStd.ParseFeatureCollection(r.Buildings.ToJsonString());
        var bbox = GeoBbox.FromGeometry(parcel.Geometry);
        return new SiteSubjectData
        {
            Provider = r.Provider,
            City = (r.City ?? "").Trim(),
            State = (r.State ?? state ?? "").Trim().ToUpperInvariant(),
            Lat = r.Lat,
            Lon = r.Lon,
            StreetName = r.StreetName,
            Parcel = parcel,
            Buildings = buildings,
            ParcelBbox = bbox
        };
    }

    /// <summary>
    /// 查询附近地块/建筑/道路。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近要素集合。</returns>
    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var bbox = PadBbox(subject.ParcelBbox, Policy.NearbyPadDegrees);
        var parcels = await QueryNearbyParcelsAsync(bbox, cancellationToken);
        var buildings = GeoJsonStd.ParseFeatureCollection((await _osm.QueryAsync(bbox, cancellationToken)).ToJsonString());
        var roads = await _roads.QueryRoadsGeoJsonAsync(bbox, cancellationToken);
        return new SiteNearbyData { Parcels = parcels, Buildings = buildings, Roads = roads };
    }

    /// <summary>
    /// 统一 plan 计算（英尺局部坐标系）。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>计算后的 plan。</returns>
    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var parcelJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Parcel)) as JsonObject ?? new JsonObject();
        var buildingsJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Buildings)) as JsonObject ?? new JsonObject();
        var plan = await _planBuilder.BuildAsync(
            parcelJson,
            buildingsJson,
            subject.StreetName,
            Policy.DefaultFrontSetbackFt,
            Policy.DefaultRearSetbackFt,
            Policy.DefaultSideSetbackFt,
            Policy.DefaultHouseSepFt,
            cancellationToken);
        if (plan is null) throw new LookupProviderException(502, "Plan 计算失败");
        return plan;
    }

    /// <summary>
    /// 查询 bbox 内附近地块（NY 税务地块公开图层）。
    /// </summary>
    /// <param name="bboxLonLat">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近地块 FeatureCollection。</returns>
    private async Task<GeoJsonFeatureCollection> QueryNearbyParcelsAsync(double[] bboxLonLat, CancellationToken cancellationToken)
    {
        try
        {
            var minLon = bboxLonLat[0];
            var minLat = bboxLonLat[1];
            var maxLon = bboxLonLat[2];
            var maxLat = bboxLonLat[3];

            var url =
                "https://gisservices.its.ny.gov/arcgis/rest/services/NYS_Tax_Parcels_Public/FeatureServer/1/query"
                + $"?f=geojson&geometryType=esriGeometryEnvelope&inSR=4326&geometry={minLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{minLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + "&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=4326&resultRecordCount=200";

            var http = _httpFactory.CreateClient("arcgis");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            using var resp = await http.GetAsync(url, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return EmptyFc();
            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(text);
            return GeoJsonStd.ParseFeatureCollection(doc.RootElement);
        }
        catch
        {
            return EmptyFc();
        }
    }

    /// <summary>
    /// 对 bbox 做简单扩展（用于 nearby 查询）。
    /// </summary>
    /// <param name="bbox">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="pad">扩展量（度）。</param>
    /// <returns>扩展后的 bbox。</returns>
    private static double[] PadBbox(double[] bbox, double pad)
    {
        if (bbox.Length < 4) return bbox;
        return new[] { bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad };
    }

    /// <summary>
    /// 空 FeatureCollection。
    /// </summary>
    private static GeoJsonFeatureCollection EmptyFc() => new() { Type = "FeatureCollection", Features = new List<GeoJsonFeature>() };
}

/// <summary>
/// 新泽西（NJ）地区适配器：使用 NJ 官方地块 provider，并组合附近要素（地块/建筑/道路）。
/// </summary>
public sealed class NewJerseyRegionAdapter : ISiteRegionAdapter
{
    private readonly NewJerseyOfficialLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;

    /// <summary>
    /// 创建新泽西地区适配器。
    /// </summary>
    /// <param name="provider">NJ 官方地块查询 provider。</param>
    /// <param name="osm">OSM 建筑查询服务。</param>
    /// <param name="httpFactory">HttpClient 工厂（需注册 <c>arcgis</c> client）。</param>
    /// <param name="roads">Overpass 道路查询服务。</param>
    /// <param name="planBuilder">统一 plan 计算器。</param>
    public NewJerseyRegionAdapter(
        NewJerseyOfficialLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
    }

    /// <summary>
    /// 地区标识。
    /// </summary>
    public string Name => "newjersey";

    /// <summary>
    /// 新泽西地区默认策略参数。
    /// </summary>
    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 20,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    /// <summary>
    /// 仅处理 NJ 地址。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <returns>NJ 返回 true。</returns>
    public bool CanHandle(string address) => LookupUtils.ExtractState(address) == "NJ";

    /// <summary>
    /// 获取目标地块与建筑集合，并计算 bbox。
    /// </summary>
    /// <param name="address">单行地址。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>目标数据；找不到地块返回 null。</returns>
    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
    {
        var state = LookupUtils.ExtractState(address);
        var r = await _provider.LookupAsync(address, state, cancellationToken);
        if (r is null) return null;
        var parcel = GeoJsonStd.ParseFeature(r.Parcel.ToJsonString());
        var buildings = GeoJsonStd.ParseFeatureCollection(r.Buildings.ToJsonString());
        var bbox = GeoBbox.FromGeometry(parcel.Geometry);
        return new SiteSubjectData
        {
            Provider = r.Provider,
            City = (r.City ?? "").Trim(),
            State = (r.State ?? state ?? "").Trim().ToUpperInvariant(),
            Lat = r.Lat,
            Lon = r.Lon,
            StreetName = r.StreetName,
            Parcel = parcel,
            Buildings = buildings,
            ParcelBbox = bbox
        };
    }

    /// <summary>
    /// 查询附近地块/建筑/道路。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近要素集合。</returns>
    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var bbox = PadBbox(subject.ParcelBbox, Policy.NearbyPadDegrees);
        var parcels = await QueryNearbyParcelsAsync(bbox, cancellationToken);
        var buildings = GeoJsonStd.ParseFeatureCollection((await _osm.QueryAsync(bbox, cancellationToken)).ToJsonString());
        var roads = await _roads.QueryRoadsGeoJsonAsync(bbox, cancellationToken);
        return new SiteNearbyData { Parcels = parcels, Buildings = buildings, Roads = roads };
    }

    /// <summary>
    /// 统一 plan 计算（英尺局部坐标系）。
    /// </summary>
    /// <param name="subject">目标地块数据。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>计算后的 plan。</returns>
    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
    {
        var parcelJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Parcel)) as JsonObject ?? new JsonObject();
        var buildingsJson = JsonNode.Parse(GeoJsonStd.ToJson(subject.Buildings)) as JsonObject ?? new JsonObject();
        var plan = await _planBuilder.BuildAsync(
            parcelJson,
            buildingsJson,
            subject.StreetName,
            Policy.DefaultFrontSetbackFt,
            Policy.DefaultRearSetbackFt,
            Policy.DefaultSideSetbackFt,
            Policy.DefaultHouseSepFt,
            cancellationToken);
        if (plan is null) throw new LookupProviderException(502, "Plan 计算失败");
        return plan;
    }

    /// <summary>
    /// 查询 bbox 内附近地块（NJ cadastral 图层）。
    /// </summary>
    /// <param name="bboxLonLat">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="cancellationToken">取消/超时。</param>
    /// <returns>附近地块 FeatureCollection。</returns>
    private async Task<GeoJsonFeatureCollection> QueryNearbyParcelsAsync(double[] bboxLonLat, CancellationToken cancellationToken)
    {
        try
        {
            var minLon = bboxLonLat[0];
            var minLat = bboxLonLat[1];
            var maxLon = bboxLonLat[2];
            var maxLat = bboxLonLat[3];

            var url =
                "https://maps.nj.gov/arcgis/rest/services/Framework/Cadastral/MapServer/0/query"
                + $"?f=geojson&geometryType=esriGeometryEnvelope&inSR=4326&geometry={minLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{minLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + "&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=4326&resultRecordCount=200";

            var http = _httpFactory.CreateClient("arcgis");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            using var resp = await http.GetAsync(url, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return EmptyFc();
            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(text);
            return GeoJsonStd.ParseFeatureCollection(doc.RootElement);
        }
        catch
        {
            return EmptyFc();
        }
    }

    /// <summary>
    /// 对 bbox 做简单扩展（用于 nearby 查询）。
    /// </summary>
    /// <param name="bbox">经纬度 bbox（minLon,minLat,maxLon,maxLat）。</param>
    /// <param name="pad">扩展量（度）。</param>
    /// <returns>扩展后的 bbox。</returns>
    private static double[] PadBbox(double[] bbox, double pad)
    {
        if (bbox.Length < 4) return bbox;
        return new[] { bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad };
    }

    /// <summary>
    /// 空 FeatureCollection。
    /// </summary>
    private static GeoJsonFeatureCollection EmptyFc() => new() { Type = "FeatureCollection", Features = new List<GeoJsonFeature>() };
}

/// <summary>
/// Bellevue（WA）地区适配器 — 使用 King County 官方数据源，应用 Bellevue ADU 退尺规则。
/// 前院 20 ft / 后院 5 ft / 侧院 5 ft（符合 HB 1337 及 BCC 20.20.025）。
/// </summary>
public sealed class BellevueRegionAdapter : ISiteRegionAdapter
{
    private static readonly System.Text.RegularExpressions.Regex CityPattern =
        new(@"\bBellevue\s*,", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ZipPrefixes = { "98004", "98005", "98006", "98007", "98008", "98009" };

    private readonly KingCountyLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<BellevueRegionAdapter> _logger;

    public BellevueRegionAdapter(
        KingCountyLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<BellevueRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    public string Name => "bellevue";

    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 5,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    public bool CanHandle(string address)
    {
        if (LookupUtils.ExtractState(address) != "WA") return false;
        if (CityPattern.IsMatch(address)) return true;
        foreach (var zip in ZipPrefixes)
            if (address.Contains(zip, StringComparison.Ordinal)) return true;
        return false;
    }

    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchSubjectAsync(_provider, address, cancellationToken);

    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchNearbyAsync(_osm, _httpFactory, _roads, subject, Policy.NearbyPadDegrees, _logger, cancellationToken);

    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.BuildPlanAsync(_planBuilder, subject, Policy, cancellationToken);
}

/// <summary>
/// Redmond（WA）地区适配器 — 使用 King County 官方数据源，应用 Redmond ADU 退尺规则。
/// 前院 20 ft / 后院 5 ft / 侧院 5 ft（符合 HB 1337 及 Redmond Municipal Code Title 21）。
/// </summary>
public sealed class RedmondRegionAdapter : ISiteRegionAdapter
{
    private static readonly System.Text.RegularExpressions.Regex CityPattern =
        new(@"\bRedmond\s*,", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ZipPrefixes = { "98052", "98053", "98073" };

    private readonly KingCountyLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<RedmondRegionAdapter> _logger;

    public RedmondRegionAdapter(
        KingCountyLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<RedmondRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    public string Name => "redmond";

    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 5,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    public bool CanHandle(string address)
    {
        if (LookupUtils.ExtractState(address) != "WA") return false;
        if (CityPattern.IsMatch(address)) return true;
        foreach (var zip in ZipPrefixes)
            if (address.Contains(zip, StringComparison.Ordinal)) return true;
        return false;
    }

    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchSubjectAsync(_provider, address, cancellationToken);

    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchNearbyAsync(_osm, _httpFactory, _roads, subject, Policy.NearbyPadDegrees, _logger, cancellationToken);

    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.BuildPlanAsync(_planBuilder, subject, Policy, cancellationToken);
}

/// <summary>
/// Kirkland（WA）地区适配器 — 使用 King County 官方数据源，应用 Kirkland ADU 退尺规则。
/// 前院 20 ft / 后院 5 ft / 侧院 5 ft（符合 HB 1337 及 Kirkland Municipal Code 21A）。
/// </summary>
public sealed class KirklandRegionAdapter : ISiteRegionAdapter
{
    private static readonly System.Text.RegularExpressions.Regex CityPattern =
        new(@"\bKirkland\s*,", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ZipPrefixes = { "98033", "98034", "98083" };

    private readonly KingCountyLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<KirklandRegionAdapter> _logger;

    public KirklandRegionAdapter(
        KingCountyLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<KirklandRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    public string Name => "kirkland";

    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 5,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    public bool CanHandle(string address)
    {
        if (LookupUtils.ExtractState(address) != "WA") return false;
        if (CityPattern.IsMatch(address)) return true;
        foreach (var zip in ZipPrefixes)
            if (address.Contains(zip, StringComparison.Ordinal)) return true;
        return false;
    }

    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchSubjectAsync(_provider, address, cancellationToken);

    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchNearbyAsync(_osm, _httpFactory, _roads, subject, Policy.NearbyPadDegrees, _logger, cancellationToken);

    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.BuildPlanAsync(_planBuilder, subject, Policy, cancellationToken);
}

/// <summary>
/// Renton（WA）地区适配器 — 使用 King County 官方数据源，应用 Renton ADU 退尺规则。
/// 前院 20 ft / 后院 5 ft / 侧院 3 ft（符合 HB 1337 及 Renton Municipal Code 4-2）。
/// 侧院退尺为 3 ft，比 King County 标准更宽松，符合 Renton 本地规定。
/// </summary>
public sealed class RentonRegionAdapter : ISiteRegionAdapter
{
    private static readonly System.Text.RegularExpressions.Regex CityPattern =
        new(@"\bRenton\s*,", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] ZipPrefixes = { "98055", "98056", "98057", "98058", "98059" };

    private readonly KingCountyLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<RentonRegionAdapter> _logger;

    public RentonRegionAdapter(
        KingCountyLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<RentonRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    public string Name => "renton";

    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 5,
        DefaultSideSetbackFt = 3,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    public bool CanHandle(string address)
    {
        if (LookupUtils.ExtractState(address) != "WA") return false;
        if (CityPattern.IsMatch(address)) return true;
        foreach (var zip in ZipPrefixes)
            if (address.Contains(zip, StringComparison.Ordinal)) return true;
        return false;
    }

    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchSubjectAsync(_provider, address, cancellationToken);

    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchNearbyAsync(_osm, _httpFactory, _roads, subject, Policy.NearbyPadDegrees, _logger, cancellationToken);

    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.BuildPlanAsync(_planBuilder, subject, Policy, cancellationToken);
}

/// <summary>
/// King County 通用地区适配器 — 处理其他 King County WA 城市（Issaquah、Sammamish、Kirkland 等）。
/// 应用 HB 1337 标准退尺规则：前院 20 ft / 后院 5 ft / 侧院 5 ft。
/// </summary>
public sealed class KingCountyRegionAdapter : ISiteRegionAdapter
{
    private readonly KingCountyLookupProvider _provider;
    private readonly OsmBuildingsService _osm;
    private readonly IHttpClientFactory _httpFactory;
    private readonly OverpassRoadService _roads;
    private readonly PlanBuilder _planBuilder;
    private readonly ILogger<KingCountyRegionAdapter> _logger;

    public KingCountyRegionAdapter(
        KingCountyLookupProvider provider,
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        PlanBuilder planBuilder,
        ILogger<KingCountyRegionAdapter> logger)
    {
        _provider = provider;
        _osm = osm;
        _httpFactory = httpFactory;
        _roads = roads;
        _planBuilder = planBuilder;
        _logger = logger;
    }

    public string Name => "kingcounty";

    public RegionPolicy Policy => new()
    {
        DefaultFrontSetbackFt = 20,
        DefaultRearSetbackFt = 5,
        DefaultSideSetbackFt = 5,
        DefaultHouseSepFt = 5,
        AduModuleSizesFt = new List<(double w, double h)>
        {
            (16, 37.5),
            (32, 37.5),
            (16, 45),
            (16, 52.5)
        },
        NearbyPadDegrees = 0.0012
    };

    public bool CanHandle(string address)
    {
        if (LookupUtils.ExtractState(address) != "WA") return false;
        // Match any King County city that isn't handled by a more specific adapter
        foreach (var city in XBuildApi.Lookup.Providers.KingCountyLookupProvider.KingCountyCities)
            if (address.IndexOf(city, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // 980xx ZIP codes (King County non-Seattle)
        if (System.Text.RegularExpressions.Regex.IsMatch(address, @"\b980\d{2}\b")) return true;
        return false;
    }

    public async Task<SiteSubjectData?> FetchSubjectAsync(string address, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchSubjectAsync(_provider, address, cancellationToken);

    public async Task<SiteNearbyData> FetchNearbyAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.FetchNearbyAsync(_osm, _httpFactory, _roads, subject, Policy.NearbyPadDegrees, _logger, cancellationToken);

    public async Task<LookupPlan> BuildPlanAsync(SiteSubjectData subject, CancellationToken cancellationToken)
        => await KingCountyAdapterHelper.BuildPlanAsync(_planBuilder, subject, Policy, cancellationToken);
}

/// <summary>
/// King County 系列适配器的共享实现逻辑（减少重复代码）。
/// </summary>
internal static class KingCountyAdapterHelper
{
    public static async Task<SiteSubjectData?> FetchSubjectAsync(
        XBuildApi.Lookup.Providers.KingCountyLookupProvider provider,
        string address,
        CancellationToken cancellationToken)
    {
        var state = LookupUtils.ExtractState(address);
        var r = await provider.LookupAsync(address, state, cancellationToken);
        if (r is null) return null;

        var parcel = GeoJsonStd.ParseFeature(r.Parcel.ToJsonString());
        var buildings = GeoJsonStd.ParseFeatureCollection(r.Buildings.ToJsonString());
        var bbox = GeoBbox.FromGeometry(parcel.Geometry);

        return new SiteSubjectData
        {
            Provider = r.Provider,
            City = (r.City ?? "").Trim(),
            State = (r.State ?? state ?? "").Trim().ToUpperInvariant(),
            Lat = r.Lat,
            Lon = r.Lon,
            StreetName = r.StreetName,
            Parcel = parcel,
            Buildings = buildings,
            ParcelBbox = bbox
        };
    }

    public static async Task<SiteNearbyData> FetchNearbyAsync(
        OsmBuildingsService osm,
        IHttpClientFactory httpFactory,
        OverpassRoadService roads,
        SiteSubjectData subject,
        double nearbyPadDegrees,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var bbox = PadBbox(subject.ParcelBbox, nearbyPadDegrees);
        var parcels = await QueryNearbyParcelsAsync(httpFactory, bbox, logger, cancellationToken);

        GeoJsonFeatureCollection buildings;
        GeoJsonFeatureCollection roadsFc;
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            buildings = GeoJsonStd.ParseFeatureCollection((await osm.QueryAsync(bbox, timeoutCts.Token)).ToJsonString());
        }
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            roadsFc = await roads.QueryRoadsGeoJsonAsync(bbox, timeoutCts.Token);
        }

        return new SiteNearbyData { Parcels = parcels, Buildings = buildings, Roads = roadsFc };
    }

    public static async Task<LookupPlan> BuildPlanAsync(
        PlanBuilder planBuilder,
        SiteSubjectData subject,
        RegionPolicy policy,
        CancellationToken cancellationToken)
    {
        var parcelJson = System.Text.Json.Nodes.JsonNode.Parse(GeoJsonStd.ToJson(subject.Parcel)) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        var buildingsJson = System.Text.Json.Nodes.JsonNode.Parse(GeoJsonStd.ToJson(subject.Buildings)) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject();
        var plan = await planBuilder.BuildAsync(
            parcelJson,
            buildingsJson,
            subject.StreetName,
            policy.DefaultFrontSetbackFt,
            policy.DefaultRearSetbackFt,
            policy.DefaultSideSetbackFt,
            policy.DefaultHouseSepFt,
            cancellationToken);
        if (plan is null) throw new XBuildApi.Lookup.LookupProviderException(502, "Plan 计算失败");
        return plan;
    }

    private static async Task<GeoJsonFeatureCollection> QueryNearbyParcelsAsync(
        IHttpClientFactory httpFactory,
        double[] bboxLonLat,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var minLon = bboxLonLat[0];
            var minLat = bboxLonLat[1];
            var maxLon = bboxLonLat[2];
            var maxLat = bboxLonLat[3];

            var url =
                "https://gisdata.kingcounty.gov/arcgis/rest/services/OpenDataPortal/property__parcel_area/FeatureServer/439/query"
                + string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "?f=geojson&geometryType=esriGeometryEnvelope&inSR=4326&geometry={0},{1},{2},{3}&spatialRel=esriSpatialRelIntersects&outFields=*&returnGeometry=true&outSR=4326&resultRecordCount=200",
                    minLon, minLat, maxLon, maxLat);

            var http = httpFactory.CreateClient("arcgis");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));

            logger.LogInformation("KingCounty nearby parcels 请求 url={Url}", url);
            using var resp = await http.GetAsync(url, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode) return EmptyFc();
            var text = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return GeoJsonStd.ParseFeatureCollection(doc.RootElement);
        }
        catch
        {
            return EmptyFc();
        }
    }

    private static double[] PadBbox(double[] bbox, double pad)
    {
        if (bbox.Length < 4) return bbox;
        return new[] { bbox[0] - pad, bbox[1] - pad, bbox[2] + pad, bbox[3] + pad };
    }

    private static GeoJsonFeatureCollection EmptyFc()
        => new() { Type = "FeatureCollection", Features = new List<GeoJsonFeature>() };
}

/// <summary>
/// GeoJSON 几何 bbox 计算工具。
/// </summary>
static class GeoBbox
{
    /// <summary>
    /// 从 GeoJSON Geometry 计算经纬度 bbox。
    /// </summary>
    /// <param name="geometry">GeoJSON geometry。</param>
    /// <returns>经纬度 bbox（minLon,minLat,maxLon,maxLat）。若无法计算则返回 0 值 bbox。</returns>
    public static double[] FromGeometry(GeoJsonGeometry geometry)
    {
        if (geometry.Coordinates.ValueKind != JsonValueKind.Array)
            return new[] { 0d, 0d, 0d, 0d };

        var minLon = double.PositiveInfinity;
        var minLat = double.PositiveInfinity;
        var maxLon = double.NegativeInfinity;
        var maxLat = double.NegativeInfinity;

        void Walk(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Array) return;
            if (el.GetArrayLength() == 0) return;

            var first = el[0];
            if (first.ValueKind == JsonValueKind.Number && el.GetArrayLength() >= 2)
            {
                var lon = el[0].GetDouble();
                var lat = el[1].GetDouble();
                if (double.IsFinite(lon) && double.IsFinite(lat))
                {
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
                return;
            }

            foreach (var child in el.EnumerateArray())
                Walk(child);
        }

        Walk(geometry.Coordinates);
        if (!double.IsFinite(minLon) || !double.IsFinite(minLat) || !double.IsFinite(maxLon) || !double.IsFinite(maxLat))
            return new[] { 0d, 0d, 0d, 0d };

        return new[] { minLon, minLat, maxLon, maxLat };
    }
}
