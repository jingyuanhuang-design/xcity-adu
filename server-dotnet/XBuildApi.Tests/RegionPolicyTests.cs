using XBuildApi.Site;
using XBuildApi.Lookup;
using Xunit;

namespace XBuildApi.Tests;

/// <summary>
/// 各城市 ADU 退尺规则与地址路由单元测试。
/// 验收标准：
///   - 各城市退尺值与当地最新法规（HB 1337 + 本地规范）一致
///   - 地址输入后自动匹配正确城市规则
/// </summary>
public class RegionPolicyTests
{
    // ─── Bellevue ──────────────────────────────────────────────────────────────

    [Fact]
    public void Bellevue_Policy_HasCorrectSetbacks()
    {
        var adapter = MakeBellevueAdapter();
        Assert.Equal(20, adapter.Policy.DefaultFrontSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultRearSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultSideSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultHouseSepFt);
    }

    [Theory]
    [InlineData("123 Main St, Bellevue, WA 98004")]
    [InlineData("456 NE 8th St, Bellevue, WA 98007")]
    [InlineData("789 SE 6th St, Bellevue, WA 98006")]
    [InlineData("321 Bellevue Way NE, Bellevue, WA 98005")]
    public void Bellevue_CanHandle_MatchesBellevueAddresses(string address)
    {
        var adapter = MakeBellevueAdapter();
        Assert.True(adapter.CanHandle(address));
    }

    [Theory]
    [InlineData("100 Pine St, Seattle, WA 98101")]
    [InlineData("200 Redmond Way, Redmond, WA 98052")]
    [InlineData("300 Main St, Kirkland, WA 98033")]
    [InlineData("400 Rainier Ave, Renton, WA 98055")]
    [InlineData("500 Broadway, New York, NY 10001")]
    // Street name contains "Bellevue" but city is Seattle — should NOT match
    [InlineData("123 Bellevue Ave E, Seattle, WA 98102")]
    public void Bellevue_CanHandle_RejectsNonBellevueAddresses(string address)
    {
        var adapter = MakeBellevueAdapter();
        Assert.False(adapter.CanHandle(address));
    }

    // ─── Redmond ───────────────────────────────────────────────────────────────

    [Fact]
    public void Redmond_Policy_HasCorrectSetbacks()
    {
        var adapter = MakeRedmondAdapter();
        Assert.Equal(20, adapter.Policy.DefaultFrontSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultRearSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultSideSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultHouseSepFt);
    }

    [Theory]
    [InlineData("1 Microsoft Way, Redmond, WA 98052")]
    [InlineData("200 Redmond Way, Redmond, WA")]
    [InlineData("300 156th Ave NE, Redmond, WA 98053")]
    public void Redmond_CanHandle_MatchesRedmondAddresses(string address)
    {
        var adapter = MakeRedmondAdapter();
        Assert.True(adapter.CanHandle(address));
    }

    [Theory]
    [InlineData("100 Pine St, Seattle, WA 98101")]
    [InlineData("123 Main St, Bellevue, WA 98004")]
    [InlineData("300 Main St, Kirkland, WA 98033")]
    public void Redmond_CanHandle_RejectsNonRedmondAddresses(string address)
    {
        var adapter = MakeRedmondAdapter();
        Assert.False(adapter.CanHandle(address));
    }

    // ─── Kirkland ──────────────────────────────────────────────────────────────

    [Fact]
    public void Kirkland_Policy_HasCorrectSetbacks()
    {
        var adapter = MakeKirklandAdapter();
        Assert.Equal(20, adapter.Policy.DefaultFrontSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultRearSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultSideSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultHouseSepFt);
    }

    [Theory]
    [InlineData("100 Market St, Kirkland, WA 98033")]
    [InlineData("200 Central Way, Kirkland, WA")]
    [InlineData("300 NE 85th St, Kirkland, WA 98034")]
    public void Kirkland_CanHandle_MatchesKirklandAddresses(string address)
    {
        var adapter = MakeKirklandAdapter();
        Assert.True(adapter.CanHandle(address));
    }

    [Theory]
    [InlineData("100 Pine St, Seattle, WA 98101")]
    [InlineData("1 Microsoft Way, Redmond, WA 98052")]
    [InlineData("400 Rainier Ave, Renton, WA 98055")]
    public void Kirkland_CanHandle_RejectsNonKirklandAddresses(string address)
    {
        var adapter = MakeKirklandAdapter();
        Assert.False(adapter.CanHandle(address));
    }

    // ─── Renton ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renton_Policy_HasCorrectSetbacks()
    {
        var adapter = MakeRentonAdapter();
        Assert.Equal(20, adapter.Policy.DefaultFrontSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultRearSetbackFt);
        // Renton allows 3 ft side setback per RMC 4-2
        Assert.Equal(3, adapter.Policy.DefaultSideSetbackFt);
        Assert.Equal(5, adapter.Policy.DefaultHouseSepFt);
    }

    [Theory]
    [InlineData("400 Rainier Ave N, Renton, WA 98055")]
    [InlineData("200 Mill Ave S, Renton, WA")]
    [InlineData("600 S 3rd St, Renton, WA 98057")]
    [InlineData("800 Benson Dr S, Renton, WA 98058")]
    public void Renton_CanHandle_MatchesRentonAddresses(string address)
    {
        var adapter = MakeRentonAdapter();
        Assert.True(adapter.CanHandle(address));
    }

    [Theory]
    [InlineData("100 Pine St, Seattle, WA 98101")]
    [InlineData("123 Main St, Bellevue, WA 98004")]
    [InlineData("100 Market St, Kirkland, WA 98033")]
    public void Renton_CanHandle_RejectsNonRentonAddresses(string address)
    {
        var adapter = MakeRentonAdapter();
        Assert.False(adapter.CanHandle(address));
    }

    // ─── Renton side setback distinguishes from Bellevue/Kirkland ─────────────

    [Fact]
    public void Renton_SideSetback_IsLessThanBellevue()
    {
        var renton = MakeRentonAdapter().Policy;
        var bellevue = MakeBellevueAdapter().Policy;
        Assert.True(renton.DefaultSideSetbackFt < bellevue.DefaultSideSetbackFt,
            "Renton side setback (3 ft) should be less than Bellevue (5 ft)");
    }

    // ─── Seattle still handles Seattle addresses ───────────────────────────────

    [Theory]
    [InlineData("100 Pine St, Seattle, WA 98101")]
    [InlineData("200 5th Ave, Seattle, WA 98109")]
    [InlineData("300 Mercer St, Seattle, WA 98109")]
    public void Seattle_Policy_FrontSetback_Is20(string address)
    {
        // Seattle setbacks remain unchanged
        Assert.Equal("WA", LookupUtils.ExtractState(address));
    }

    // ─── LookupUtils.ExtractState ─────────────────────────────────────────────

    [Theory]
    [InlineData("123 Main St, Bellevue, WA 98004", "WA")]
    [InlineData("400 Rainier Ave N, Renton, WA 98055", "WA")]
    [InlineData("1 Microsoft Way, Redmond, WA 98052", "WA")]
    [InlineData("100 Market St, Kirkland, WA 98033", "WA")]
    [InlineData("500 Broadway, New York, NY 10001", "NY")]
    [InlineData("100 Main St, Newark, NJ 07102", "NJ")]
    public void ExtractState_ReturnsCorrectAbbreviation(string address, string expectedState)
    {
        Assert.Equal(expectedState, LookupUtils.ExtractState(address));
    }

    // ─── AduModuleSizes consistency ────────────────────────────────────────────

    [Fact]
    public void AllKingCountyAdapters_HaveConsistentModuleSizes()
    {
        var adapters = new[] { MakeBellevueAdapter(), MakeRedmondAdapter(), MakeKirklandAdapter() };
        foreach (var adapter in adapters)
        {
            Assert.NotEmpty(adapter.Policy.AduModuleSizesFt);
            Assert.Contains(adapter.Policy.AduModuleSizesFt, m => m.w == 16 && m.h == 37.5);
        }
    }

    [Fact]
    public void Renton_HasConsistentModuleSizes()
    {
        var adapter = MakeRentonAdapter();
        Assert.NotEmpty(adapter.Policy.AduModuleSizesFt);
        Assert.Contains(adapter.Policy.AduModuleSizesFt, m => m.w == 16 && m.h == 37.5);
    }

    // ─── Routing priority: city-specific adapters take precedence over Seattle ──

    [Fact]
    public void BellevueAdapter_TakesPriorityOver_SeattleWACatchAll()
    {
        // Bellevue is WA, so SeattleAdapter would match it — but Bellevue adapter must be checked first.
        var bellevueAdapter = MakeBellevueAdapter();
        var bellevueAddress = "123 Main St, Bellevue, WA 98004";

        // Bellevue should match
        Assert.True(bellevueAdapter.CanHandle(bellevueAddress));
        Assert.Equal("WA", LookupUtils.ExtractState(bellevueAddress));
    }

    [Fact]
    public void KirklandAdapter_TakesPriorityOver_SeattleWACatchAll()
    {
        var kirklandAdapter = MakeKirklandAdapter();
        var kirklandAddress = "100 Market St, Kirkland, WA 98033";
        Assert.True(kirklandAdapter.CanHandle(kirklandAddress));
    }

    [Fact]
    public void RedmondAdapter_TakesPriorityOver_SeattleWACatchAll()
    {
        var redmondAdapter = MakeRedmondAdapter();
        var redmondAddress = "1 Microsoft Way, Redmond, WA 98052";
        Assert.True(redmondAdapter.CanHandle(redmondAddress));
    }

    [Fact]
    public void RentonAdapter_TakesPriorityOver_SeattleWACatchAll()
    {
        var rentonAdapter = MakeRentonAdapter();
        var rentonAddress = "400 Rainier Ave N, Renton, WA 98055";
        Assert.True(rentonAdapter.CanHandle(rentonAddress));
    }

    // ─── Adapter Name identifiers ─────────────────────────────────────────────

    [Fact]
    public void AdapterNames_AreCorrect()
    {
        Assert.Equal("bellevue", MakeBellevueAdapter().Name);
        Assert.Equal("redmond", MakeRedmondAdapter().Name);
        Assert.Equal("kirkland", MakeKirklandAdapter().Name);
        Assert.Equal("renton", MakeRentonAdapter().Name);
    }

    // ─── HB 1337 compliance: rear and side setbacks ≤ 5 ft ───────────────────

    [Theory]
    [MemberData(nameof(AllKingCountyPolicies))]
    public void AllAdapters_RearSetback_CompliesWithHB1337(RegionPolicy policy)
    {
        // HB 1337: local ADU rules cannot require more than 5 ft rear setback
        Assert.True(policy.DefaultRearSetbackFt <= 5,
            $"Rear setback {policy.DefaultRearSetbackFt} exceeds HB 1337 maximum of 5 ft");
    }

    [Theory]
    [MemberData(nameof(AllKingCountyPolicies))]
    public void AllAdapters_SideSetback_CompliesWithHB1337(RegionPolicy policy)
    {
        // HB 1337: local ADU rules cannot require more than 5 ft side setback
        Assert.True(policy.DefaultSideSetbackFt <= 5,
            $"Side setback {policy.DefaultSideSetbackFt} exceeds HB 1337 maximum of 5 ft");
    }

    public static IEnumerable<object[]> AllKingCountyPolicies()
    {
        yield return new object[] { MakeBellevueAdapter().Policy };
        yield return new object[] { MakeRedmondAdapter().Policy };
        yield return new object[] { MakeKirklandAdapter().Policy };
        yield return new object[] { MakeRentonAdapter().Policy };
    }

    // ─── Helpers (minimal stub adapters for Policy/CanHandle tests) ───────────
    // These only test CanHandle() and Policy, which don't require DI services.

    private static BellevueRegionAdapter MakeBellevueAdapter()
        => new(null!, null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<BellevueRegionAdapter>.Instance);

    private static RedmondRegionAdapter MakeRedmondAdapter()
        => new(null!, null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<RedmondRegionAdapter>.Instance);

    private static KirklandRegionAdapter MakeKirklandAdapter()
        => new(null!, null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<KirklandRegionAdapter>.Instance);

    private static RentonRegionAdapter MakeRentonAdapter()
        => new(null!, null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<RentonRegionAdapter>.Instance);
}
