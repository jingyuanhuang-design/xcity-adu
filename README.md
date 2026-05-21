# XBuild

本仓库包含 XBuild 的前后端工程：

- 后端（.NET）：[server-dotnet](server-dotnet)
- 前端（Figma/Vite React）：[figma](figma)
- 其他目录：辅助脚本/素材/实验文件等

## 目录结构

```
server-dotnet/   # .NET Web API（XBuildApi）
figma/           # 前端（Vite + React）
```

## 快速启动（Docker）

使用 Docker Compose 一键启动完整应用：

```bash
# 构建并启动所有服务
docker-compose up

# 后台运行
docker-compose up -d

# 重新构建并启动
docker-compose up --build

# 停止服务
docker-compose down
```

服务访问地址：
- 前端：http://localhost
- 后端 API：http://localhost:5000

### 单独构建服务

```bash
# 仅构建后端
docker-compose build backend

# 仅构建前端
docker-compose build frontend

# 仅启动后端
docker-compose up backend

# 仅启动前端
docker-compose up frontend
```

### 开发模式（支持热重载）

使用开发版 Docker Compose 配置，支持代码热重载：

```bash
# 启动开发环境
docker-compose -f docker-compose.dev.yml up

# 后台运行
docker-compose -f docker-compose.dev.yml up -d

# 停止开发环境
docker-compose -f docker-compose.dev.yml down
```

开发环境访问地址：
- 前端开发服务器：http://localhost:5173
- 后端 API：http://localhost:5000

代码修改会自动触发重新编译和热重载。

也可以使用本地环境进行开发（见下方"后端"和"前端"章节）。

## 后端：server-dotnet

- 解决方案：`server-dotnet/XBuildApi.sln`
- 项目：`server-dotnet/XBuildApi/XBuildApi.csproj`

本地运行（示例）：

```bash
cd server-dotnet
dotnet restore
dotnet run --project XBuildApi/XBuildApi.csproj
```

## 前端：figma

- 工程入口：`figma/index.html`
- 源码：`figma/src`

本地运行（示例）：

```bash
cd figma
npm install
npm run dev
```

构建（示例）：

```bash
cd figma
npm run build
```

## 说明

- 前端/后端均依赖本地环境配置（例如端口、上游服务、AI 开关等）。请根据实际部署环境调整对应配置文件（如 `server-dotnet/XBuildApi/appsettings.json`、`figma/.env.*`）。
- `figma/public/static-assets/` 下包含部分静态资源（例如模型文件相关配置）。

## 后端逻辑（server-dotnet）

后端是一个 ASP.NET Core Web API，核心职责是：根据地址拉取地块/周边要素，统一到可计算的坐标系，计算退尺/可建区/车库通道/可放置性，并把结果以 GeoJSON + 结构化 computed 字段返回给前端渲染。

### 主要接口

- `/api/lookup`（POST）：主入口，输入地址，返回目标地块、附近要素与计算结果  
  代码：[LookupController.cs](server-dotnet/XBuildApi/Controllers/LookupController.cs)
- `/api/geocode`（GET）：地址→经纬度（给前端用于定位/输入体验）  
  代码：[GeocodeController.cs](server-dotnet/XBuildApi/Controllers/GeocodeController.cs)
- `/api/suggest`（GET）：地址联想（建议列表）  
  代码：[GeocodeController.cs](server-dotnet/XBuildApi/Controllers/GeocodeController.cs)
- `/api/tiles/osm/{z}/{x}/{y}.png`（GET）：OSM PNG 瓦片代理（统一 CORS/缓存）  
  代码：[TilesController.cs](server-dotnet/XBuildApi/Controllers/TilesController.cs)
- `/api/health`（GET）：健康检查  
  代码：[HealthController.cs](server-dotnet/XBuildApi/Controllers/HealthController.cs)

### /api/lookup 的流水线

`LookupController` 内部会按地区选择适配器（Seattle / NewYork / NewJersey 等），并执行：

1. **FetchSubject**：抓取目标地块（parcel）与地块内建筑轮廓（subject buildings）
2. **FetchNearby**：抓取附近地块/道路/建筑（nearby parcels/roads/buildings）
3. **BuildPlan**：调用 `PlanBuilder` 把原始 GeoJSON 统一到“英尺局部坐标系”，并计算基础几何  
4. **ComputeComputed**：基于 plan + 地区政策参数（退尺/通道等）输出前端可直接消费的 `computed` 字段
5. **AiParcelInfo（可选）**：若开启 AI，上游返回 zoning/lotDimensions 等文本信息；失败则回退到 plan 的保底值  

返回的数据结构在：[SiteModels.cs](server-dotnet/XBuildApi/Site/SiteModels.cs)（`SiteLookupResponse` / `SiteComputed` 等）。

### PlanBuilder 做了什么

`PlanBuilder` 的核心是把 “经纬度多边形” 变成 “可计算、可对齐街道方向的英尺坐标系”，并为后续可建区/摆放算法提供输入：

- **坐标转换**：地块 polygon（lon/lat）→ WebMercator（米）→ 旋转对齐街道方向 → 转英尺并规范化到局部坐标
- **旋转角选择**：优先用“最近道路段”的方向作为对齐基准；没有道路时回退到“最长边”方向
- **结构识别**：从地块内建筑中识别主屋（house）/车库（garage）等角色（用于 house separation、driveway 等规则）
- **产出 plan**：`plan.lot.widthFt/heightFt/polygon`、`plan.structures`、`plan.buildablePolygon/buildableRings`、cutouts 等

代码入口：[PlanBuilder.cs](server-dotnet/XBuildApi/Lookup/PlanBuilder.cs)

### computed 字段（前端直接用来画图/交互）

`ComputeComputed` 会把 plan 进一步聚合成前端要用的结构：

- `rotationDeg`：前端地图/3D 的统一旋转口径
- `setbacksFt`：退尺线（前/后/侧等）
- `drivewayCorridorFt`：车库通道（用于展示/扣除障碍）
- `buildablePolygonFt` + `buildableArea`（GeoJSON）：真实可建区域（可为多边形/含 holes）
- `aduPlacementArea`（GeoJSON）：ADU “中心点可落点区域”（用于快速判断可放置区域）
- `aduFits`：不同户型尺寸是否可放下（前端用来启用/禁用按钮）
- `rulerLinesFt`：后端建议的标尺线（前端可直接画标尺）

组装位置：[LookupController.cs](server-dotnet/XBuildApi/Controllers/LookupController.cs)

## 前端逻辑（figma）

前端是 Vite + React（TypeScript），核心职责是：发起 `/api/lookup` 获取数据、做 2D/3D 可视化、提供交互（切换视图/旋转/测量/模块选择/外挂选择等），并把“可建区、退尺、既有建筑、ADU 摆放”直观呈现出来。

### 数据流

1. 用户输入地址（会使用 `/api/suggest` / `/api/geocode` 辅助）
2. 调用 `/api/lookup` 获取 `SiteLookupResponse`
3. `lookup` 会缓存到 `localStorage['xhomes.lookup']`（便于刷新/跨页面复用）
4. 2D/3D 组件从 `lookup` 渲染地图、可建区、障碍、ADU、GLB 等

相关 API 封装：`figma/src/api/*`

### 关键页面/组件

- Studio 总控（tab/状态/布局）：[DesignStudio.tsx](figma/src/components/studio/DesignStudio.tsx)
- 可行性页面（2D/3D、可建区、退尺、模块/外挂选择）：[SiteFeasibility.tsx](figma/src/components/studio/SiteFeasibility.tsx)
- 设计/预览页面（2D/3D、GLB 覆盖、测量工具）：[SiteVisualizer.tsx](figma/src/components/studio/SiteVisualizer.tsx)

### 2D 渲染（SVG Map）

2D 视图主要把 GeoJSON（lon/lat）投影到屏幕：

- 先把地块/建筑/道路点集投影到 WebMercator（米），再映射到画布像素（px）
- 使用 `computed.rotationDeg` 在屏幕坐标系旋转，使“街道方向/地块方向”一致
- 在 SVG 上绘制：地块边界、可建区（含洞）、退尺、cutouts、既有建筑、ADU 外框等

### 3D 渲染（Isometric + GLB）

3D 视图使用等轴测投影做“轻量 3D”：

- 既有建筑/车库：由地块内建筑轮廓挤出成体块（高度按规则估算）
- ADU：用体块或 GLB 模型展示
- GLB 覆盖：Three.js + `GLTFLoader` 加载模块 glb，并与等轴测场景对齐叠加  
  代码：[SiteVisualizer.tsx](figma/src/components/studio/SiteVisualizer.tsx)

### 测量工具（2D/3D）

测量尺支持吸附与高亮：

- 2D：基于地块/建筑/ADU 的边与角点吸附
- 3D：在等轴测投影里拾取地面/屋顶/立面，并对边角吸附
- GLB：支持 raycast 命中，并对三角面顶点/边进行小范围吸附（用于测量模型内部细节）

距离换算口径：

- 水平（x/y）：优先用 `bboxMercator.scale`（px↔米）换算到英尺
- 垂直（z）：按 ft 口径的 `FT_TO_UNIT` 进行换算
