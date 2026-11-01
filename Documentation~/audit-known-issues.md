# `com.unity.virtualmesh` 缺陷审计报告

| 项 | 值 |
|---|---|
| 审计对象 | `Packages/com.unity.virtualmesh`，`0.2.0-preview`（2025-12-12） |
| 审计时环境 | Unity `6000.6.7f1`，URP `17.6.0`，ShaderGraph `17.6.0`，Mathematics `1.4.0`（`packages-lock.json` 实际解析） |
| 包声明环境 | Unity `6000.3`，URP `17.3.0`，ShaderGraph `17.3.0`，Mathematics `1.3.3` |
| 审计方法 | **纯静态代码阅读 + 算术推导**。未编译、未运行、未抓帧 |
| 条目数 | 37（P0×6 / P1×9 / P2×17 / P3×5） |
| 归属 | 本文件为本仓库追加内容，**不属于**上游 `com.unity.virtualmesh`。重新克隆上游包前请先备份（建议 fork 到自己的分支） |

> ⚠️ **免责**：我没有可用的 Unity 编辑器实例（`Library/` 不存在，编辑器不在本机常见路径），因此**所有"实际现象"都是推导，不是观测**。每条都给了可执行的判定实验，请在 Phase 0 逐条确认后再据此排期。置信度标注见下。

## 0. 阅读指南

**证据等级**

| 标记 | 含义 |
|---|---|
| `[确认]` | 代码事实，可直接引用（预处理器错误、缺失校验、硬编码常量、字符串拼写错误）。看代码即可判定，无需运行 |
| `[推导]` | 由代码算术或控制流推出，逻辑闭合，但依赖运行时行为，需实验确认 |
| `[待验证]` | 依赖外部 API 版本或平台行为，本环境无法核对 |

**严重级别**

| 级别 | 定义 |
|---|---|
| `P0` | 产生**错误画面**或**错误数据**（含只在 release 包出现的那类） |
| `P1` | 崩溃、资源泄漏、静默失效、超过某个规模后必然出错 |
| `P2` | 性能、画质、可维护性、可观测性 |
| `P3` | 文档、打包、许可、工程结构 |

---

## 1. 结论摘要

### 1.1 最关键的三条

1. **`P0-01` 是"只在 release 包出现"的双重绘制 bug**。`#if UNITY_EDITOR || DEVELOPMENT_BUILD` 把 `if` 条件整体编译掉了，导致非 Development 构建里虚拟网格被画两遍（`Update` 里一次 + RenderGraph `DrawPass` 里一次）。编辑器里表现完全正常，所以极易漏到出包才被发现。
2. **`P0-02` + `P1-04` 指向同一件事：索引"计量单位"在 baker 与 runtime 之间有 3× 的不一致。**同一批常量被同时当成"三角形数"和"索引值数"使用。这不只是命名问题——两处必然有一处按 3× 错，其中一处（可见性位图）会表现为**三角形空洞**。
3. **这个包的"能用"是内容规模与配置的巧合，不是设计保证**。256 页 / 1600 cluster/页 / 128 材质 / 20 m 加载距离 / 固定 4 cascade 阴影 / 固定 256² 深度金字塔——六个互相耦合的硬上限，全部无断言、无校验、无降级。

### 1.2 建议的动手顺序（详见 §6）

```
批次 A（1 天）  P0-01, P1-05, P1-08, P2-13     ← 纯 bug，零架构风险，立即做
批次 B（2 天）  P0-03, P0-04, P0-05, P0-06, P1-01 ← 正确性，改动局部
批次 C（3 天）  P0-02 + P1-04（同一个专题，见 §4）← 需要实验 + 决策，见 §4.4
批次 D（2 天）  P1-03, P1-06, P1-07            ← 资源生命周期
批次 E（持续）  P2 系列
```

---

## 2. 全量清单

| ID | 级别 | 置信 | 区域 | 一句话 | 位置 |
|---|---|---|---|---|---|
| P0-01 | P0 | 确认 | Runtime | release 构建下虚拟网格被绘制两次 | `VirtualMeshManager.cs:1372-1377` |
| P0-02 | P0 | 推导 | Compute | 三角形可见性位图与三角形缓冲索引空间不一致，跨 cluster 别名 | `DXCVisibilityPasses.compute:235,323,392` |
| P0-03 | P0 | 确认 | Compute | 阴影剔除硬编码 2×2 四分块，非 4 cascade 时误剔除 | `ComputeCommon.hlsl:174-202` |
| P0-04 | P0 | 确认 | Compute | 视锥剔除只用 4 个侧平面，无 near/far | `ComputeCommon.hlsl:110-118` |
| P0-05 | P0 | 确认 | Baker | 烘焙会覆盖源 Mesh 的法线/切线/包围盒 | `VirtualMeshBakerAPI.cs:375-377` |
| P0-06 | P0 | 确认 | Baker | 顶点色被烘焙但从不写入运行时数据 | `VirtualMeshBakerAPI.cs:818-833` |
| P1-01 | P1 | 确认 | Runtime | 初始化失败泄漏 + 无"未烘焙"优雅降级 | `VirtualMeshManager.cs:1301-1322,1060-1064` |
| P1-02 | P1 | 确认 | Runtime | 回读失败被静默吞掉，无计数无告警 | `VirtualMeshManager.cs:500-501` |
| P1-03 | P1 | 推导 | Runtime | 页读取异常被当作加载成功（拷垃圾 + 置 Loaded），异常延迟到 shutdown | `VirtualMeshManager.cs:1199,1065-1075` |
| P1-04 | P1 | 推导 | Baker/Runtime | 索引计量单位 3× 不一致（同 P0-02 专题） | `VirtualMeshBakerAPI.cs:809,866` |
| P1-05 | P1 | 确认 | Baker/Compute | 超过 128 个材质时静默错画 | `ComputeCommon.hlsl:2`, `DXCVisibilityPasses.compute:579-639` |
| P1-06 | P1 | 确认 | RenderFeature | 在 `RecordRenderGraph` 里分配/释放 GPU 缓冲 | `VirtualMeshRenderFeature.cs:196` → `:960-976` |
| P1-07 | P1 | 确认 | Runtime | 深度金字塔是工程共享资产，非 per-camera RG 资源 | `DepthPyramid.renderTexture`, `VirtualMeshRenderFeature.cs:19` |
| P1-08 | P1 | 确认 | Runtime | `CameraLoadDistanceThreshold` 默认 20 m 且是"不加载"硬阈值 | `VirtualMeshManager.cs:154` |
| P1-09 | P1 | 待验证 | Editor | `ShaderGraphImporter.GetShaderText` 为 internal API，跨版本即断 | `ShaderGraphHelper.cs:10` |
| P2-01 | P2 | 确认 | RenderFeature | 每帧 `FindPass(string)` / `GetInterpolatedProbe` / `new RenderTargetIdentifier[]` | `VirtualMeshRenderFeature.cs:306,320,343,607` |
| P2-02 | P2 | 确认 | Runtime | 每页加载 `new byte[]` + `MemoryStream` + 逐 u32 解析 | `VirtualMeshManager.cs:214-224,260-282` |
| P2-03 | P2 | 确认 | Runtime | 上传缓冲按整段 stride 复制，而非实际长度 | `VirtualMeshManager.cs:1226,1231-1298` |
| P2-04 | P2 | 推导 | Compute | group 包围球用 AABB 外接球（半径大 √3），且不随所选 LOD 收紧 | `DXCVisibilityPasses.compute:125` |
| P2-05 | P2 | 确认 | Baker | 烘焙 O(n²)（`List.IndexOf`）+ 每次扩容整体 MemCpy + 每 mesh 6 次托管拷贝 | `VirtualMeshBakerAPI.cs:580,682,1415-1430` |
| P2-06 | P2 | 确认 | Baker | 无材质/几何合并，draw call 数 = 材质数（`materialID<<16` 留空未实现） | `VirtualMeshBakerAPI.cs:858` |
| P2-07 | P2 | 确认 | Format | `.vmeshdata` 磁盘零压缩 | `VirtualMeshBakerAPI.cs:1080-1105` |
| P2-08 | P2 | 确认 | Baker | shader 转换靠字符串替换 + 静默跳过不支持的 shader | `VirtualMeshBakerAPI.cs:248-297,974-985` |
| P2-09 | P2 | 确认 | Runtime | `.vmesh` 格式无 magic / 无版本 / 无校验 / 无长度 | `VirtualMeshBakerAPI.cs:1049-1105` |
| P2-10 | P2 | 确认 | 全局 | 常量命名与真实含义不符（`IndexValuePageStride` 实为三角形数） | `VirtualMeshManager.cs:301,997` |
| P2-11 | P2 | 确认 | 全局 | 无 profiler 粒度、无剔除率/页命中率/上传字节统计 | 全局 |
| P2-12 | P2 | 确认 | 工程 | 无任何测试（无 `Tests/`、无测试 asmdef） | — |
| P2-13 | P2 | 确认 | Baker | 清理缓存时 `Shaders.meta` 拼写漏掉 → 删掉子目录 .meta | `VirtualMeshBakerAPI.cs:172` |
| P2-14 | P2 | 确认 | Baker | `Virtual Mesh/Setup Scene` 是空方法；无增量/无 dirty/无 Undo | `VirtualMeshBaker.cs:22-25` |
| P2-15 | P2 | 确认 | Runtime | 用"只有 bounds、没有顶点"的 Mesh 触发 shadow caster；默认关闭 | `VirtualMeshManager.cs:710-714,351` |
| P2-16 | P2 | 确认 | RenderFeature | 所有虚拟网格阴影共用单一 `customPassMaterial` → alpha clip / 自定义阴影丢失 | `VirtualMeshRenderFeature.cs:159-161` |
| P2-17 | P2 | 确认 | Baker | 透明物体烘焙后 renderQueue 未复制，顺序不可控（README 已承认） | `VirtualMeshBakerAPI.cs:1187-1188` |
| P2-18 | P2 | 确认 | Runtime | 阴影滞后一帧（本帧阴影用上帧剔除结果），快移相机下阴影错位 | `DXCVisibilityPasses.compute:531` |
| P2-19 | P2 | 待验证 | Compute | 页优先级 bitonic 排序的方向判定可能差 1 位，只影响加载优先级不影响正确性 | `DXCVisibilityPasses.compute:46-52` |
| P3-01 | P3 | 确认 | 文档 | `Documentation~/` 无 index/toc；`samples: []` 无示例场景 | `package.json:20` |
| P3-02 | P3 | 确认 | 文档 | 无 byte-level 格式规范文档 | — |
| P3-03 | P3 | 确认 | 许可 | Unity Companion License，公开托管衍生源码需自行确认边界 | `LICENSE.md` |
| P3-04 | P3 | 确认 | 打包 | `package.json` 缺 `unityRelease`/`documentationUrl`/`changelogUrl`/`licensesUrl` | `package.json` |
| P3-05 | P3 | 待验证 | 兼容 | 声明 17.3 / 1.3.3，实际解析 17.6 / 1.4.0，未做版本护栏 | `package.json:8-10` |

---

## 3. 详细条目

### 3.1 P0 — 错误画面 / 错误数据

---

#### P0-01 `[确认]` release 构建下虚拟网格被绘制两次

**现象**：Editor 与 Development Build 表现正常；**非 Development 的 release 包**里虚拟网格的顶点/三角形/GBuffer 写入翻倍，GPU 时间上升但没有明显视觉差异（opaque z-test 掩盖了它）。

**证据** `VirtualMeshManager.cs:1372-1377`

```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!IsDebugViewEnabled && !IsInternalDrawPassEnabled)
#endif
                {
                    DrawVirtualMesh();
                }
```

**根因**：`#if` 包住了 `if` 语句本身，而不是语句体。两个宏都未定义时（release），剩下的代码是：

```csharp
                {
                    DrawVirtualMesh();   // 无条件执行
                }
```

而 `IsInternalDrawPassEnabled` 默认 `true`（`VirtualMeshManager.cs:346`），`VirtualMeshRenderFeature.AddRenderPasses` 在 `true` 时还会 enqueue `m_DrawPass`（`VirtualMeshRenderFeature.cs:88-94`）。于是 `Update()` 里的 `Graphics.RenderPrimitivesIndexedIndirect`（走 URP 默认 opaque pass）与 `DrawPass` 各画一遍。

**复现**
1. Build Settings 关闭 Development Build，打 Windows/Vulkan release 包；
2. RenderDoc / PIX 抓一帧，统计虚拟网格 draw 的 indexCount 总和；
3. 与 Editor 内同一相机对比 → release 下翻倍。
4. 反向确认：把 `IsInternalDrawPassEnabled` 设为 `false` 后 release 恢复正常（因为 `AddRenderPasses` 不再 enqueue `DrawPass`）。

**期望 vs 实际**：期望"内部绘制 pass 开关决定由谁画"，实际"release 下两者都画"。

**修法**
```csharp
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!IsDebugViewEnabled && !IsInternalDrawPassEnabled)
#else
                if (!IsInternalDrawPassEnabled)
#endif
                {
                    DrawVirtualMesh();
                }
```
更稳的写法是把 `#if` 收进条件表达式内部，避免同类错误再次发生。顺手补一条 release-only 的 smoke test（见 §5）。

**工作量** 5 分钟，风险低。

---

#### P0-02 `[推导]` 三角形可见性位图的索引空间与三角形缓冲不一致

**现象（推导）**：开启 TAA 或静止相机时，画面出现**成片闪烁的三角形空洞**；视野移动后空洞位置改变。因为被误判为"上一帧已可见"的三角形在两趟绘制里**都不会被画**。

**证据链**

| 位置 | 代码 | 用到的单位 |
|---|---|---|
| `DXCVisibilityPasses.compute:235` | `TriangleBuffer.Load((triangleID + InstanceDataLDS.x) * 4)` | `x` 必须是**三角形序号**（该缓冲 1 uint = 1 三角形） |
| `DXCVisibilityPasses.compute:323` | `uint triangleGlobalID = mad(triangleID, 3, InstanceDataLDS.x) / 3;` | 等价于 `floor(x/3) + triangleID` |
| `DXCVisibilityPasses.compute:324` | `if (!ReadTriangleVisibility(triangleGlobalID))` | 未可见才写入压缩索引 |
| `DXCVisibilityPasses.compute:392` | `if (ReadTriangleVisibility(mad(triangleID, 3, InstanceDataLDS.x) / 3))` | 上一帧可见才重建索引 |
| `VirtualMeshBakerAPI.cs:804` | `SetData(dataStart + 0, indexValueCount + indexValueOffset)` | 写入的就是 `x` |
| `VirtualMeshBakerAPI.cs:809` | `indexValueOffset += k_PackIndices ? optimizeIndices[i].Count / 3 : ...` | 每次增加**三角形数**（≤64），确认 `x` 是三角形序号 |

于是 cluster `k` 占用的位区间是 `[x_k/3, x_k/3 + 63]`，而 `x_{k+1} - x_k ≤ 64`，即 `x_{k+1}/3 - x_k/3 ≤ 22`。**相邻 cluster 的位区间重叠至少 42 bit**。

**为什么这不像是笔误**：位图容量 `VirtualMeshManager.cs:1017` 是 `stride × loadablePages / 96` uints，即 `stride × pages / 3` bits；而 `/96 = /32/3` 恰好等于"1 bit / 三角形再压 3 倍"。也就是说**位索引的 `/3` 与容量的 `/96` 是互相自洽的**——这是有意的 3× 内存压缩，代价就是别名。

**后果链**：别名 → 误判"上帧已可见" → `RefreshDepthPyramidPass`（pass 4）不重建该三角形索引，`TriangleOcclusionPass`（pass 3）因 `!ReadTriangleVisibility` 也不写入 → **该三角形这一帧完全不画**。

**复现（最小实验，强烈建议先做）**
1. 单页、单 group、单材质：用一个 200 万三角形的平面（会切出大量 64 三角形 cluster）；
2. 相机固定，`IsDebugViewEnabled` 关闭，把 `CompactedIndexBuffer` 里每帧的实际 `indexCount` 之和打到 `OnGUI`；
3. 对比"理论可见三角形数"（可从 `DispatchArgsBuffer[0]` 的 cluster 数 × 64 估算）与实际；
4. 然后把 `DXCVisibilityPasses.compute:323` 改成 `triangleGlobalID = InstanceDataLDS.x + triangleID`（**同时**把位图尺寸 ×3）再跑一遍，若空洞消失即证实。

**修法（三选一，按推荐排序）**

| 方案 | 做法 | 内存 | 评价 |
|---|---|---|---|
| **(c) 推荐** | 位图按 **cluster 槽位**分配：`bit = clusterGlobalIndex * 64 + triangleID`，尺寸 = `1600 × loadablePages × 64` bits = **1.6 MB**（128 页时），完全无别名、尺寸可预测、与索引单位无关 | +1.6 MB | 一举把 P0-02 与 P1-04 的单位问题解耦；推荐 |
| (a) | 位索引改 `x + triangleID`，位图 ×3 | +3.2 MB | 最小改动，但仍依赖 `x` 的单位约定 |
| (b) | 让 `x` 对齐到 3 的倍数并同步改 `TriangleBuffer` 读法 | 0 | 同时动烘焙与三处 shader，回归面最大 |

**工作量** 方案 (c) 约 1 天（含验证），方案 (a) 约 2 小时。

**关联** P1-04、§4。

---

#### P0-03 `[确认]` 阴影剔除硬编码 2×2 四分块

**现象（推导）**：把 URP 的主光阴影 cascade 数设为 1/2/3 时，虚拟网格的投射阴影**大面积缺失**（只保留约 1/4 的投影几何）；设为 4 时正常。

**证据** `ComputeCommon.hlsl:174-202`

```hlsl
#if defined(VMESH_SHADOW_CASCADE_0)
    screenCorner = mul(_MainLightWorldToShadow[0], float4(corner, 1.0)).xyz;
    return screenCorner.x < 0.0 || screenCorner.x > 0.5 ||
           screenCorner.y < 0.0 || screenCorner.y > 0.5;
// ... CASCADE_1/2/3 各占另外三个 0.5×0.5 象限
```

这里的 `0.5` 只在 **4 cascade = 2×2 atlas** 时成立。URP 在 `cascadeCount == 1` 时用整张 shadow map（1×1），`== 2` 时上下两半（1×2），`== 3` 时 `2×2` 但有一格空置。而此时 shader 仍按 2×2 判定，于是 3/4 的屏幕区域被判为 `outOfFrustum` 并剔除。

**为什么会漏**：`ShadowUtils.ExtractDirectionalLightMatrix` 已经把每级 cascade 的 `offsetX/offsetY/resolution` 算好放在 `ShadowSliceData` 里（`VirtualMeshRenderFeature.cs:198-206`），但 compute 侧拿不到，只能用编译期宏硬编码。

**复现**
1. 场景里放一个大的虚拟网格立方体，地面上应该有阴影；
2. URP Asset → Lighting → Main Light → Shadow Type: Soft/ Hard，Cascades: **No Cascades**（1 级）；
3. 对比 `IsEnabled = false`（普通 MeshRenderer）与 `true` 的阴影覆盖；
4. 再分别用 2 / 3 / 4 Cascades 复测。

**修法**：把 `ShadowSliceData` 的 `offset`/`scale` 通过常量缓冲传进 `ClusterOcclusionPass`/`ShadowTriangleOcclusionPass`，替换 `#define`：

```hlsl
// 每 cascade 一个 float4 offsetScale，或直接传 worldToShadow * uvScaleBias
half4 cullRect = _VmeshCascadeCullRect[cascadeIndex]; // xy=min, zw=max, 单位 [0,1]
return screenCorner.x < cullRect.x || screenCorner.x > cullRect.z || ...
```
同时把 `VMESH_SHADOW_CASCADE_n` 的 `multi_compile` 从 4 个全局关键字改为 `ShadowCascadeCount` 的 uniform 分支——顺带减少 4 个变体。

**工作量** 半天（含复测 1/2/3/4 cascade）。**关联** P2-18。

---

#### P0-04 `[确认]` 视锥剔除只用 4 个侧平面

**证据** `ComputeCommon.hlsl:110-118`

```hlsl
bool FrustumCullTriangle(half3 p0, half3 p1, half3 p2)
{
	return TestClipPlane(p0, p1, p2, 0) || TestClipPlane(p0, p1, p2, 1)
        || TestClipPlane(p0, p1, p2, 2) || TestClipPlane(p0, p1, p2, 3);
}
```
只有索引 `0..3`（左右上下），没有 `4`（近）与 `5`（远）。

**影响**
- 相机**背后**的 cluster/页不会被平面剔除，只能靠后面的深度金字塔测试与 `CameraLoadDistanceThreshold` 兜底；
- 而 `ProjectAABBToScreenSpace` 又把 `w` 强制 clamp 到近裁面（`:146` `screenCorners[i].w = max(screenCorners[i].w, _ProjectionParams.y)`），使背后物体的投影矩形被"拉到"屏幕上，可能被判为可见 → **加载无用页**、把无关 cluster 加入绘制列表；
- 对 `PageOcclusionPass` 影响最大：页一旦被请求就会占一个槽位（只有 128 个）并触发磁盘 I/O。

**复现**：相机朝 +Z，把大片虚拟网格放在 -Z 方向 5 m 处（完全在背后），`OnGUI` 里观察 `Memory page status: X loaded` 是否非 0。

**修法**：`FrustumCullTriangle/Sphere` 补 `4`、`5`；注意 `unity_CameraWorldClipPlanes` 的平面顺序与 `UNITY_REVERSED_Z` 下 near/far 的比较方向（现有 `TestClipPlane` 已用 `<= 0` 统一处理，直接加索引即可）。

**工作量** 2 小时，但对流式的收益可能很大（见 Phase 6 的预算模型）。

---

#### P0-05 `[确认]` 烘焙会覆盖源 Mesh 的法线/切线/包围盒

**证据** `VirtualMeshBakerAPI.cs:375-377`

```csharp
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
```
`mesh` 来自 `filter.sharedMesh`（`:362`），是**工程里的 Mesh 资产本身**，不是副本。

**影响**
1. 覆盖导入时烘焙好的法线（`.fbx` 的 import normals / 自定义 hard edge 全部丢失，变成平滑法线）→ 烘焙后的虚拟网格**看起来和原模型不一样**，而且不可逆（要么重导入，要么 `git checkout` 资产）；
2. `RecalculateBounds` 覆盖原始 bounds；
3. 编辑器里会 dirty 资产，配合 `AssetDatabase.SaveAssets`/自动保存就可能写回磁盘；
4. 多 submesh 时每个 filter 循环都会重算一遍，白费时间。

**复现**：准备一个带明显硬边的模型（如箱子），烘焙前后在 Scene 视图对比法线；或烘焙后 `git status` 看 `.fbx` 的 `.meta`/导入设置是否变化、重开工程看是否被改。

**修法**
```csharp
var mesh = UnityEngine.Object.Instantiate(filter.sharedMesh);  // 临时副本
// ... 用完
UnityEngine.Object.DestroyImmediate(mesh);
```
并且**不要**无条件 `RecalculateNormals`：只有当 `mesh.normals.Length == 0` 时才补，否则沿用导入值（这本身也影响烘焙质量——虚拟几何体的法线应该尽量来自原资产）。

**工作量** 2 小时（另加"只在缺失时补算"的策略）。

---

#### P0-06 `[确认]` 顶点色被烘焙但从不写入运行时数据

**证据**
- 烘焙侧确实读了顶点色并做了默认值兜底：`VirtualMeshBakerAPI.cs:422` `var defaultVertexColor = Color.cyan;`、`:432` `color = new Vector3(color0.r, color0.g, color0.b)`；
- 但写回 GPU 的 4 个 attribute uint 里没有 color 槽位（`:818-833`）：

| attribute index | 内容 |
|---|---|
| `+0` | 八面体法线 |
| `+1` | 八面体切线 |
| `+2` | `f16(uv1.x) << 16 \| f16(uv0.x)` |
| `+3` | `f16(uv1.y) << 16 \| f16(uv0.y)` |

- 顶点解包侧同样没有 color（`Common.hlsl:22-39`、`LitForwardPass.hlsl:170-181`）。

**影响**：`Vertex.color` 只流向**占位 Mesh**（`VirtualMeshBakerAPI.cs:1241`），所以占位网格可能是青色而虚拟网格是白的；任何依赖顶点色（tint、遮罩、风吹摆动权重借道 color）的美术资产在虚拟网格上失效，且**不会报任何错**。

**修法（二选一，需先决策）**
- **不支持**（推荐先这么做）：在 `CheckSupportedShader` 里检测材质 keyword/`shader` 是否依赖 `_VERTEX_COLOR`，命中就 **fail-fast 报错**并列出资产，而不是静默丢数据；
- **支持**：attribute 从 4 uint 扩到 5 uint（每顶点 +4 B），或把 color 的 3×8bit 挤进 `vdata.y` 的高位（`tangentSign` 只占 1 bit，还剩 15 bit 不够放 24 bit 色，需要压缩到 15 bit 或改布局）。这会动到 `P2-09` 的格式版本号。

**工作量** 不支持的方案 2 小时；支持的方案 1 天。

---

### 3.2 P1 — 崩溃 / 泄漏 / 静默失效

---

#### P1-01 `[确认]` 初始化失败泄漏 + 无"未烘焙"优雅降级

**两个独立问题**

**(a) 未烘焙时 `OnEnable` 直接抛异常**：`VirtualMeshManager.cs:758-759`

```csharp
            string path = $"{ResourcePathDefinitions.virtualMeshDataPath}/metadata.vmesh";
            using (var stream = BetterStreamingAssets.OpenRead(path));
```
与 `RequestMaterials`（`:849-854` 会判 null）不同，这里不检查存在性 → `FileNotFoundException` 从 `OnEnable` 逃逸。Unity 会把组件留在半禁用状态，`m_Initialized` 仍为 `false`，但 `AllocateData` 可能已经跑过一半。

**(b) 分阶段初始化失败会泄漏**：`OnEnable` 的顺序是 `RequestMetadata → AllocateData → RequestMaterials → AllocateDrawBuffers → AllocateShadowDrawBuffers → RequestHeaders → m_Initialized = true`（`:1301-1322`）。若 `RequestMaterials` 失败（AssetBundle 没打出来），`AllocateData` 分配的 `NativeArray<MemoryPageStatus>`、`NativeArray<uint> m_MeshHeader`、十几个 `GraphicsBuffer` 全部留在内存。因为 `OnDisable → DisposeData` 第一行就是：

```csharp
            if (!m_Initialized)   // :1062
                return;
```

**复现**
1. 删掉 `Assets/StreamingAssets/VirtualMeshData` 整个目录，进 Play → 控制台 1 条异常，无任何可读提示；
2. 只保留 `metadata.vmesh`，删掉 `Materials/` → 用 Memory Profiler 观察退出 Play 后 `GraphicsBuffer` 是否仍驻留（或反复进出 Play 10 次看显存曲线）。

**修法**
```csharp
bool m_Allocated;   // 与 m_Initialized 解耦
...
bool ok = false;
try {
    ok = RequestMetadata() && AllocateData_() && RequestMaterials()
         && AllocateDrawBuffers_() && AllocateShadowDrawBuffers_() && RequestHeaders();
} catch (Exception e) {
    Debug.LogError($"[Virtual Mesh] Initialization failed: {e}");
}
if (!ok) { DisposeData(); m_Initialized = false; return; }
m_Allocated = true; m_Initialized = true;
```
并且给 `DisposeData` 加 `force` 参数（或把早退条件改成 `if (!m_Allocated) return;`），所有 `Release()` 前判空。

**工作量** 半天。

---

#### P1-02 `[确认]` 回读失败被静默吞掉

**证据** `VirtualMeshManager.cs:495-501`

```csharp
        public void FeedbackReadbackCallback(AsyncGPUReadbackRequest request)
        {
            if (!m_Initialized || m_HeaderJobRunning) return;
            if (request.hasError) return;      // :500-501
```

**注意**：这**不会**让流式永久停摆——`VisibilityPass` 每帧都会重新 `cmd.RequestAsyncReadback`（`VirtualMeshRenderFeature.cs:484`），失败一帧不影响下一帧。所以它不是致命 bug。

**真正的问题是零可观测性**：连续失败（某些 Android 驱动上 `AsyncGPUReadback` 会静默失败）表现为"画面永远不加载、日志一片干净"。这也是 `P2-11` 的一部分。

**修法**：加失败计数 + 连续 N 次失败时 `Debug.LogError` 一次并置一个 `m_StreamingError` 标志；`OnGUI` 显示。

**工作量** 1 小时。

---

#### P1-03 `[推导]` 页读取异常被当作"加载成功"，异常延迟到 shutdown 才抛出

**证据**
- 状态置 `Loading` 与 job 调度：`VirtualMeshManager.cs:1152-1180`
- 收尾只判 `IsCompleted`，不判成败：`:1199`

```csharp
                if (m_DataJobRunning[i] && m_LoadMeshDataJobHandles[i].IsCompleted)
                {
                    DispatchCopies(i);
                    m_MemoryPageStatus[m_PageIDs[i]] = MemoryPageStatus.Loaded;   // 无条件置 Loaded
```

- job 里的 IO 无保护：`:256` `using (var stream = BetterStreamingAssets.OpenRead(assetPath))`
- 异常最终在 `DisposeData` 的 `Complete()` 处抛出：`:1067-1074`

**推导**：Unity 的 Job 语义是"异常在 `Complete()`/`IsCompleted` 访问时重新抛出"。这里 `IsCompleted` 为 true 后代码**直接把页标成 `Loaded` 并 `DispatchCopies` 拷贝一份未写入的上传缓冲**（即上一个页的残留数据）。结果是：页读失败 → 画面上出现**上一页的几何**（数据错乱），而唯一的错误信息在退出 Play / 卸载场景时以异常形式出现，与真凶相距很远。

**复现**：bake 一个 2 页以上的场景，手动删掉 `00000002.vmeshdata`，进 Play 观察是否出现重复几何；退出 Play 看是否抛 `FileNotFoundException`。

**修法**
```csharp
if (m_DataJobRunning[i] && m_LoadMeshDataJobHandles[i].IsCompleted)
{
    try { m_LoadMeshDataJobHandles[i].Complete(); }
    catch (Exception e) { m_MemoryPageStatus[m_PageIDs[i]] = MemoryPageStatus.Unloaded;
                          Debug.LogError($"[Virtual Mesh] page {m_PageIDs[i]} failed: {e}"); 
                          m_DataJobRunning[i] = false; continue; }
    ...
}
```
并且 job 内部用 `File.Exists`/`TryOpen` 而不是裸 `OpenRead`。

**工作量** 2 小时。

---

#### P1-04 `[推导]` 索引计量单位 3× 不一致

单独列一条是因为它可以**不经 P0-02** 就造成越界。完整推导见 **§4**，这里只给结论：

`indexValueOffset` 在 baker 里按**三角形**累加（`VirtualMeshBakerAPI.cs:809` `Count / 3`），因此 `IndexValuePageStride = max(三角形数/页)`。但 runtime 把它同时当作：

| 用途 | 需要什么单位 | 按 stride=三角形数 是否成立 |
|---|---|---|
| `m_IndexBuffer` 尺寸（`:1014`） | 三角形（1 uint/三角形） | ✅ |
| `m_CompactedIndexBuffer` 尺寸（`:966`） | **索引值**（`StoreCompactedIndices` 每三角形写 3 uint） | ❌ 疑似 3× 欠分配 |
| `DrawArgsBuffer` 的 `indexCount` / `startIndex`（`DXCVisibilityPasses.compute:180,192`） | **索引值** | ❌ 前缀和会达到 3×，`startIndex` 越界 |
| `m_TriangleVisibilityBuffer` 尺寸（`:1017`） | bit（配合 `/3` 压缩） | ✅（见 P0-02） |

**判定实验（必须先做，不必等修 P0-02）**

1. 在 `StoreCompactedIndices` 入口加断言（shader 里用 `DeviceMemoryBarrier` 不适合断言，改在 CPU 侧）：
   - bake 完把 `m_PageStrideValues[1]`、`totalInstanceCount`、以及每页实际 `indexValueCount` 打到日志；
   - 进 Play 后读回 `DrawArgsBuffer`（`AsyncGPUReadback`），打印每个材质的 `indexCount` 与 `startIndex`；
   - 断言 `startIndex + indexCount <= m_CompactedIndexBuffer.count`。
2. 若断言在**第一个批量较大的场景**就失败 → P1-04 确诊，且 `P0-02` 的同源问题也基本坐实。

**修法**：把单位显式拆开并改名（见 §4.4），烘焙期与运行期各加一次断言。

**工作量** 诊断半天；修复取决于 P0-02 选择的方案。

---

#### P1-05 `[确认]` 超过 128 个材质时静默错画

**证据**
- `ComputeCommon.hlsl:2` `#define MaxMaterialCount (128)`
- `DXCVisibilityPasses.compute:579,609,631`（`OffsetScanPass` / `InitArgumentPass` / `ResetArgumentPass`）都是 `[numthreads(MaxMaterialCount, 1, 1)]`
- baker 侧对材质数量**没有任何上限检查**：`VirtualMeshBakerAPI.cs:407-415` 只是 `materials.Add(...)`

**推导**：`MaterialCount > 128` 时，`id >= 128` 的材质其 draw args 永不被清零、永不被前缀和写入。后果：`indexCount` 跨帧累积、`startIndex` 停留在旧值 → 第 128 个之后的材质**画错区域或完全不画**，而且帧间不稳定。

**复现**：造一个 150 种不同材质（可用 150 个材质实例，shader 相同）的烘焙根对象 → 观察后半部分几何缺失/闪烁。

**修法**
```csharp
if (materials.Count > 128)   // 与 MaxMaterialCount 同步，或改成读 shader 常量
    throw new InvalidOperationException(
        $"[Virtual Mesh] {materials.Count} unique materials exceeds the runtime limit of 128. " +
        "Merge materials or raise MaxMaterialCount in ComputeCommon.hlsl.");
```
更彻底的做法：`MaxMaterialCount` 改成按 `MaterialCount` 多次 dispatch（`for (offset = 0; offset < MaterialCount; offset += 128)`），把硬上限变成软上限。

**工作量** fail-fast 1 小时；软上限半天。

---

#### P1-06 `[确认]` 在 `RecordRenderGraph` 里分配/释放 GPU 缓冲

**证据**
- `VirtualMeshRenderFeature.cs:196`（在 `DrawShadowPass.RecordRenderGraph` 内）

```csharp
            VirtualMeshManager.Instance.ShadowCascadeCount = cascadeCount;
```
- setter → `AllocateShadowDrawBuffers()`（`:960-976`），内部 `Dispose()` + `new GraphicsBuffer(...)` 两个缓冲：

```csharp
            if (m_CompactedIndexBuffer != null) m_CompactedIndexBuffer.Dispose();
            m_CompactedIndexBuffer = new GraphicsBuffer(...);
            if (m_ShadowDrawArgsBuffer != null) m_ShadowDrawArgsBuffer.Dispose();
            m_ShadowDrawArgsBuffer = new GraphicsBuffer(...);
```

**影响**
1. 资源生命周期操作出现在 RG 录制期，与 RG 的资源跟踪模型冲突；
2. cascade 数变化的那一帧，`m_CompactedIndexBuffer` 内容被清空 → 该帧的 `DrawShadowPass`（用上一帧的 `ShadowDrawArgsBuffer` 结果）会画出**垃圾索引**；
3. 同时 `Update()` 里的 `DrawVirtualMesh` 也引用同一缓冲；
4. 每帧一次 `Math.Max` + 可能的 setter 调用，虽小但属于"每帧做控制流决策建资源"的模式。

**修法**
- 按 4 cascade 的**上限**一次性预分配（`m_CompactedIndexBuffer` 大小按 `4 * stride * pages`），cascade 数只影响 `startIndex` 与 draw 次数，不改缓冲尺寸；
- 或把重建挪到 `AddRenderPasses` 之前、由 `UniversalShadowData.mainLightShadowCascadesCount` 变化驱动，并在重建后跳过一帧阴影绘制。

**工作量** 半天。

---

#### P1-07 `[确认]` 深度金字塔是工程共享资产，不是 per-camera RenderGraph 资源

**证据**
- `Runtime/RenderTextures/DepthPyramid.renderTexture`：`m_Width: 256`、`m_Height: 256`、`m_MipCount: -1`（满链 9 级）、`m_EnableRandomWrite: 1`、`m_AntiAliasing: 1`、`m_UseDynamicScale: 0`
- 通过序列化引用进入 render feature：`VirtualMeshRenderFeature.cs:19` `public RenderTexture depthPyramid;`，由 `:29` 从包内路径自动回填
- 分辨率在 shader 里**双重硬编码**：`ComputeCommon.hlsl:6` `#define DepthPyramidResolution (256)`、`DepthPyramidPass.compute:6` 同一个 256

**影响**

| # | 问题 |
|---|---|
| a | 分辨率固定 256²，与屏幕无关。1080p 下遮挡剔除精度过低 → 保守/激进都会被放大；4K 移动端更明显 |
| b | 单一资产 = 全局状态。多相机（分屏、UI 相机、Scene View、反射探针相机）会**互相覆盖**同一张金字塔 |
| c | 用 `AddUnsafePass` 手写 `SetRenderTarget`（`:353-355`）绕过 RG 资源跟踪，RG 无法为它做别名/生命周期优化，也拿不到正确的 load/store |
| d | 资产 `m_AntiAliasing: 1`，但 `cameraDepth` 若开启 MSAA，材质里以 `sampler2D` 点采样的深度是**未解析**的 → MSAA 下剔除结果无意义 |
| e | 与 `m_UseDynamicScale: 0` 组合，动态分辨率（DRS）下比例失真 |

**修法**：在 RG 里 `renderGraph.CreateTexture(new TextureDesc { width = screen.x >> 2, height = screen.y >> 2, ... mipCount })`，把 `DepthPyramidResolution` 改为 `PageStrideConstants` 的一个字段（第 8 个 uint 还空着）。这同时解掉 `b`/`c`/`d`/`e`。

**工作量** 1 天（含与 `DepthPyramidPass.compute` 里 8 个 `RWTexture2D` 命名绑定的适配）。

---

#### P1-08 `[确认]` `CameraLoadDistanceThreshold` 默认 20 m，且是"不加载"的硬阈值

**证据** `VirtualMeshManager.cs:154`

```csharp
        private float m_CameraLoadDistanceThreshold = 20.0f;
```
`DXCVisibilityPasses.compute:73-78`

```hlsl
    uint tooFar = 1 - step(distance((half3)_WorldSpaceCameraPos, posCenter), posRadius + asfloat(CameraLoadDistanceThreshold));
    [branch] if (tooFar)
    {
        FeedbackBufferUAV.Store(id * 4, ((id << 1) & 0xfffff) + tooFar);
        return;
    }
```

**影响**：页中心距相机超过 `radius + 20 m` 就**直接标 `TooFar`、不加载**（不是降优先级）。真实场景里 20 m 之外的所有页都会空白。README 只在 `Documentation~/implementation.md:50` 说"这个值可以调"，没说默认值只适用于小 demo。

**复现**：跑通 demo 后把相机拉远到 50 m，观察远景消失。

**修法**：默认值改成 `float.MaxValue` 或与 `camera.farClipPlane` 关联；并把"距离"从"是否加载"降级为"加载优先级的一个权重"（预算由 `m_LoadableMemoryPageCount` 与带宽模型决定）。

**工作量** 2 小时。

---

#### P1-09 `[待验证]` `ShaderGraphImporter.GetShaderText` 是 internal API

**证据** `Editor/ShaderGraphHelper/ShaderGraphHelper.cs:10`

```csharp
            return ShaderGraphImporter.GetShaderText(path, out s_ConfiguredTextures_List, null, out s_GraphData);
```
配合 `Editor/ShaderGraphHelper/Unity.ShaderGraph.Editor_Ref.asmref` 直接挂到 `Unity.ShaderGraph.Editor` 程序集上。

**风险**：这不是公开 API。包声明支持 ShaderGraph `17.3.0`，而工程实际解析到 `17.6.0`。签名一旦变化就是**编译错误**（还算好的情况）；更糟的是行为变化（例如返回的 HLSL 里 include 路径换了、或 keyword 集合不同），导致 `ConvertShaders` 的字符串替换**静默不命中**。

**为什么我无法确认**：本机没有 Unity 编辑器安装，`Library/PackageCache` 也不存在，取不到 17.6 的 `ShaderGraphImporter.cs` 做签名比对。

**Phase 0 必须做的核对**（3 条，任意一条失败就要改方案）
1. `ShaderGraphImporter.GetShaderText` 在 17.6 是否存在、参数列表是否一致；
2. `PBRForwardPass.hlsl` 等 6 个 ShaderGraph include 路径在 17.6 是否仍是这些名字；
3. `RenderPassEvent.BeforeRenderingGbuffer` / `UniversalResourceData.gBuffer` / `CommandBufferHelpers.GetNativeCommandBuffer` 在 17.6 是否仍是这些签名。

**备用方案**：放弃 ShaderGraph 自动支持，改成"要求用户手写兼容 VMESH 的 `.shader`"，并在文档里给模板。这样虽然牺牲了易用性，但去掉了整个包里最脆的依赖。

---

### 3.3 P2 — 性能 / 画质 / 可维护性

> 这一组只给"证据 + 影响 + 修法"三行式，节省篇幅。

**P2-01 `[确认]` 每帧重复的托管开销**
`VirtualMeshRenderFeature.cs:343,345,607,609` 每帧每材质 `Material.FindPass("GBuffer"/"Universal Forward"/"ForwardLit")`（字符串比较）；`:318-327` 每帧 `LightProbes.GetInterpolatedProbe(Vector3.zero, null, out sh)`（固定采样原点，本身也是画质问题）；`:306` 每帧 `new RenderTargetIdentifier[...]`。代码里自己标了 3 个 TODO。
→ 修：烘焙期把 pass index 与 SH 系数存进材质/页数据；数组改成成员复用。

**P2-02 `[确认]` 每页加载的 IO 分配**
`VirtualMeshManager.cs:214-224` 与 `:260-282`：`new byte[stream.Length]` + `MemoryStream` + `BinaryReader` 逐 u32 读。一个满页约 1.6 MB → 每次 1.6 MB 临时分配 + 40 万次 `ReadUInt32`。
→ 修：`stream.Read(MemoryMarshal.AsBytes(nativeArray.AsSpan()))`，或直接 `UnsafeUtility.As<byte, uint>` 后按段 MemCpy。收益在页多时非常明显（GC 尖峰）。

**P2-03 `[确认]` 上传缓冲按整段 stride 复制**
`VirtualMeshManager.cs:1226` 解锁整段，`:1231-1298` 五段都按 `m_PageStrideValues[*]`（**max over pages**）复制，而不是本页实际的 `vertexValueCount` 等（这些值在 job 里已经读到了，`:1167-1175`）。
→ 修：`DispatchCopies(i, headerCounts[i])`。对不均匀的页可省 30-70% 上传带宽。

**P2-04 `[推导]` group 包围球保守**
`DXCVisibilityPasses.compute:125` `half posRadius = length(posExtent);` —— 用 AABB 半长的模长当作**外接球**半径。对近立方的 AABB，它比真正的最小包围球大最多 √3 ≈ 1.73×；`ProjectAABBToScreenSpace` 用盒、`FrustumCullSphere` 用球，两者都不紧。另外组包围盒是**叶层顶点集的并集**（`k_PackClusterGroupVertices = true` 时整组共用一个顶点缓冲），不随当前选中的 LOD 收紧。
→ 影响：**保守 → 更费三角形**（`error = factor*(dist - radius) - err`，`radius` 偏大会让 `error` 偏负，更容易满足 leaf 条件 → 倾向保留更细的 LOD）。**不是掉画质，是多画三角形**。
→ 修：烘焙期存真实紧致球（或每 LOD 层一个球），并让 `posExtent` 与 `posRadius` 分开。

**P2-05 `[确认]` 烘焙 O(n²)**
`VirtualMeshBakerAPI.cs:580,682` 每个索引做 `copyIndices.IndexOf(vertexIndex)`（192 次线性扫描/cluster）；`:1415-1430` `ReserveAdditionalUints` 每次调用都新分配 + 整体 `MemCpy`（每 group 4 次、每 cluster 4 次）；`:379-384` 每 mesh 6 次托管数组拷贝；`:106-126` `GetFilterList` 对每个非 LODGroup filter 重新遍历所有 LODGroup 且**按 GameObject 名字**比较（重名即错判）。
→ 修：`Dictionary<int,int>` 做 remap；`NativeList<uint>` 一次性预留；`HashSet<MeshRenderer>` 去重。这一步能把大场景烘焙从"小时"压到"分钟"级别。

**P2-06 `[确认]` 无材质/几何合并**
`VirtualMeshBakerAPI.cs:858` `SetGroupData(..., (uint)materialIndex << 16); // space for material merge` —— 留了位没实现。因此 draw call 数 = 材质数，且每个材质都有一次 indirect draw（哪怕只剩 1 个三角形，只靠 `MaterialVertexCounts[i] == 0` 才能跳过）。
→ 这是虚拟几何体**最主要的收益**（近完美批处理）没兑现。修：按 `(shader, keyword set)` 归组 + 运行时把多个材质的 arg 合并成一次 draw（材质分层/材质 chunk）。

**P2-07 `[确认]` 磁盘零压缩**
`.vmeshdata` 是裸 u32 流（`:1080-1105`）。顶点已经 f16 + 八面体 + 3×10bit 索引（1 uint/三角形），但完全没做页级压缩。
→ 修：按页 LZ4/zstd（要注意虚拟几何体需要"按 cluster 随机访问"，所以要么整页解压到暂存，要么像 Nanite 那样页内重排 + 分块压缩）。

**P2-08 `[确认]` shader 转换靠字符串替换 + 静默跳过**
`VirtualMeshBakerAPI.cs:248-269`（URP Lit）与 `:279-297`（ShaderGraph）：替换 include 路径，并往 `Name "GBuffer"` 后面硬插一段 `Stencil { ... }`。风险：URP 一旦改文件名/格式/换行，替换**不命中且不报错**；`Name "GBuffer"` 若已带 Stencil 块会变成重复声明。
而 `CheckSupportedShader`（`:974-985`）只认 `Universal Render Pipeline/Lit` 与 `.shadergraph`，其它 shader **静默跳过**（不列入 `materials`，不报错），烘焙"成功"但几何缺失。
→ 修：每个 `Replace` 加命中计数断言（`if (!hit) throw`）；`Name "GBuffer"` 注入改成"检测是否已有 Stencil，有则跳过"；`CheckSupportedShader` 的跳过要汇总成一份"被跳过资产清单"弹窗。

**P2-09 `[确认]` 文件格式无 magic / 版本 / 校验 / 长度**
`VirtualMeshBakerAPI.cs:1049-1105` 直接 `BinaryWriter.Write`，`VirtualMeshManager.cs:749-817` 直接 `BinaryReader.ReadUInt32` 按固定顺序解析。没有 magic、没有 `formatVersion`、没有段长度、没有 checksum、没有字节序声明。
→ 任何一端改动格式 → 静默解析出错位数据（不是报错）。这也是"改格式"（P0-06/P2-07 都需要）之前必须先补的地基。

**P2-10 `[确认]` 常量命名与真实含义不符**
`IndexValuePageStride` 实际是**三角形数/页**（因 `:809` 的 `/3`）；`TotalTriangleCount` 又把它 `/96`（`VirtualMeshManager.cs:301`）；`CopyLength` 在 shader 里是"以 4 个 uint 为一组的组数"（`:20` 的 `id * 4 >= CopyLength`）。
→ 这是 `P0-02`/`P1-04` 这类 bug 的温床。修：随 `P1-04` 一起改名（`TrianglePageStride` / `IndexValuePageStride = 3 * TrianglePageStride`）。

**P2-11 `[确认]` 无 profiler 粒度、无统计**
只有 4 个 `ProfilingSampler` 包整个 pass，10 个 kernel 全在"Visibility Pass"里；`OnGUI`（`:1388-1432`）只打页面计数。
→ 修：per-kernel `ProfilingScope` + 一个 `VirtualMeshStats`（页命中率、`Waiting`/`TooFar` 比例、每帧上传字节、cluster 剔除率、三角形剔除率、LOD 层直方图）。这一项是所有后续优化的前提。

**P2-12 `[确认]` 无测试**
没有 `Tests/` 目录、没有测试 asmdef、`package.json` 无 `testables`。可优先补的纯逻辑单测：格式序列化往返、顶点打包/解包（`PackNormal`/`PackTangent`/`PackUV` 与 `Common.hlsl` 的互逆性）、LOD 误差累加、可见性位图的唯一性（**直接覆盖 P0-02**）。

**P2-13 `[确认]` 清理缓存会删掉子目录的 `.meta`**
`VirtualMeshBakerAPI.cs:172-176`

```csharp
                            if (!file.Name.Equals("Shader.meta") &&
                                !file.Name.Equals("Materials.meta") &&
                                ...
```
目录名是 `Shaders`（`:149`），所以保护名单里的 `Shader.meta` **永远不匹配**，而真正的 `Shaders.meta` 会被 `File.Delete()` 删掉 → Unity 重新生成新 GUID → 任何引用该目录内资产的引用断开。
另：这里用 `Directory.GetFiles()` + `File.Delete()` 绕过 `AssetDatabase`，目录里若有人放了别的东西会一起没。

**P2-14 `[确认]` `Virtual Mesh/Setup Scene` 是空方法；烘焙无增量**
`VirtualMeshBaker.cs:22-25`
```csharp
        [MenuItem("Virtual Mesh/Setup Scene")]
        public static void SetupScene()
        {
        }
```
加上没有 dirty 跟踪、没有 Undo、没有增量（每次都全量重烘 256 页 + 重建两个 AssetBundle，`:1210-1214`、`:1281-1285`）。

**P2-15 `[确认]` 用"只有 bounds、没有顶点"的 Mesh 触发 shadow caster**
`VirtualMeshManager.cs:791-792` 只设 `m_BoundingMesh.bounds`，从未 `SetVertices`；`:710-714` 用 `Graphics.DrawMesh` 画它，靠"URP 认为场景里有 shadow caster"这个副作用。`IsBoundingMeshEnabled` 默认 `false`（`:351`）→ **纯虚拟网格场景默认没有阴影贴图**。
→ 修：改用 `ScriptableRenderContext.DrawShadows` 或显式注入一个空 shadow caster；同时把默认值改 `true` 并在文档里说明。

**P2-16 `[确认]` 所有虚拟网格阴影共用单一 `customPassMaterial`**
`VirtualMeshRenderFeature.cs:159-161`：`cmd.DrawProceduralIndirect(..., data.settings.customPassMaterial, 0, ...)`。所以阴影用的是 `VirtualMesh/CustomPasses` 的 ShadowCaster pass（`CustomPasses.shader:11-76`），**与材质无关**。alpha clip、双面阴影、自定义 shadow pass 全部丢失 → 阴影轮廓与主渲染不一致（树叶、栅栏这类资产会整块投影）。

**P2-17 `[确认]` 透明物体**
`VirtualMeshBakerAPI.cs:1187-1188` `new Material(shader)` + `CopyPropertiesFromMaterial` 不复制 `renderQueue`；加上固定 `BeforeRenderingGbuffer` 的绘制时机（`VirtualMeshRenderFeature.cs:91`）。README 已承认"顺序不保证"，但实际是"顺序基本没有意义"。

**P2-18 `[确认]` 阴影滞后一帧**
`DXCVisibilityPasses.compute:531` 的注释自己写了 `// triangle culling pass (for next frame's main light shadows)`。相机快移/光照快速旋转时阴影会拖一帧；在 `P1-06` 的缓冲重建帧会退化为错帧。

**P2-19 `[待验证]` 页优先级的 bitonic 排序方向判定**
`DXCVisibilityPasses.compute:46-52`

```hlsl
    for (uint j = SortLevel >> 1; j > 0; j >>= 1)
    {
        uint value = (FeedbackSortLDS[gid & ~j] >> 20 > FeedbackSortLDS[gid | j] >> 20 == (bool)(SortLevel & id)) ? ... ;
```

方向测试用的是固定的 `SortLevel & id`，而标准 bitonic 的方向应随当前比较距离 `j` 变化（且通常取 `SortLevel >> 1` 的位）。**如果确实差一位**，后果是排序结果不单调 → 页加载优先级被扭曲 → 128 个槽位的换页率上升（thrashing），表现为"相机移动时远景反复空白/加载"。**不影响画面正确性**，所以排在 P2。
→ 判定：把 `FeedbackBuffer` 读回 CPU 后自己排一遍，与 GPU 结果比对；或用 §5.3 的调试模式显示每页的 `lodLevel` 字段（高 12 bit 是投影面积量化值）。
→ 修：换成标准写法，或直接改为 `SortLevel = 2` 起步的等价实现并加 CPU 侧单测。

---

### 3.4 P3 — 文档 / 打包 / 许可

| ID | 内容 |
|---|---|
| **P3-01** | `Documentation~/` 只有 `glossary.md` 与 `implementation.md`，没有 index/toc；`package.json:20` `"samples": []`，仓库里没有可一键导入的示例场景 —— 这是"能不能被别人复现"的第一道门槛 |
| **P3-02** | 没有 byte-level 格式规范（§4 的表是我从代码反推的，上游没有）。做 `P2-09`（版本化）时顺手补上 |
| **P3-03** | `LICENSE.md`：Unity Companion License，限定"Unity 生态内使用"。公开托管**衍生源码**的边界需要自行确认（我不做法律判断）。相对稳妥的表达方式：私有仓库 + 技术文章/视频 + 只公开自己写的 diff 与文档 |
| **P3-04** | `package.json` 缺 `unityRelease`、`documentationUrl`、`changelogUrl`、`licensesUrl`、`testables` |
| **P3-05** | 声明 `17.3.0` / `1.3.3`，实际解析 `17.6.0` / `1.4.0`（`packages-lock.json:199-208`、`:73-78`）。没有 `versionDefines`/`#if` 护栏，跨版本只能靠编译失败来发现 |

---

## 4. 专题：索引计量单位与可见性位图（P0-02 + P1-04 合并推导）

这一节把 `P0-02` 与 `P1-04` 的算术摊开，因为它们是同一个根因的两个表现，**必须一起决策**。

### 4.1 baker 实际写进文件的单位

| 字段 | 代码 | 单位 |
|---|---|---|
| 每 cluster 的 `indexValueStart`(`x`) | `:804` `SetData(+0, indexValueCount + indexValueOffset)` | **三角形序号**（页内） |
| 每 cluster 的 `indexCount` | `:806` `(uint)optimizeIndices[i].Count << 24` | **索引值数**（= 3 × 三角形） |
| `indexValueOffset` 增量 | `:809` `optimizeIndices[i].Count / 3` | **三角形** |
| 页级 `indexValueCount` | `:866` `+= indexValueOffset` | **三角形数/页** |
| `metadata.maxIndexValueCount` | `:1145` `bw.Write(maxIndexValueCount)` | **三角形数/页**（max） |
| 索引数据本体 | `:780-792` 每三角形写 1 个 packed uint | 1 uint / 三角形 |

### 4.2 runtime 对同一个 `IndexValuePageStride` 的四种假设

| 消费点 | 代码 | 隐含单位 | 与"三角形"是否自洽 |
|---|---|---|---|
| 源索引缓冲尺寸 | `:1014` `stride * pages` uints | 三角形 | ✅ |
| 压缩索引缓冲尺寸 | `:966` `(cascades+1) * stride * pages` uints | **索引值** | ❌ 需要 3× |
| `DrawArgs` 的 `indexCount` | `.compute:180,192` `InterlockedAdd(materialID*20+8, indexCount)`，`indexCount = z >> 24` | **索引值** | ❌ 前缀和会到 3× |
| `DrawArgs` 的 `startIndex` | `OffsetScanPass` 把上述和的累计写进 `+8` | **索引值** | ❌ 越界 |
| 可见性位图 | `:1017` `stride * pages / 96` uints | 三角形 × /3 压缩 | ✅（靠压缩对齐，代价见 P0-02） |
| `TotalTriangleCount` | `:301` `stride * pages / 96` | 混淆（名字说三角形，算式按 bit uint） | ⚠️ |

### 4.3 结论

> **同一批常量在"三角形"与"索引值"两种单位之间来回切换，至少有一处按 3× 是错的。**
> 具体到"哪一处错"，两种自洽解都存在：
> - **解 A（stride 应为 3×）**：把 `:809`/`:866` 改成累加 `Count`（索引值），同时把索引数据本体改成 3 uint/三角形（放弃 10-bit 打包）。此时可见性位图 `/3` 变为"按索引值压缩"，仍是 3× 别名问题。
> - **解 B（stride 就是三角形数，其余三处要改）**：`m_CompactedIndexBuffer` ×3、`DrawArgs` 的 `indexCount`/`startIndex` 除以 3（改成以三角形为单位），可见性位图按 §3 的方案 (c) 重做。
>
> **我推荐解 B**：保住 1 uint/三角形的 10-bit 打包（这是内存上最大的收益），把"索引值"这一概念限制在 `StoreCompactedIndices` 内部。

### 4.4 推荐落地步骤（顺序不能换）

1. **改名**（纯机械、零风险）：`IndexValuePageStride` → `TrianglePageStride`；新增 `IndexValuePageStride = 3 * TrianglePageStride`。
2. **加断言**（不改行为）：烘焙结束时校验 `maxIndexValueCount == max(三角形数)`；运行期读回 `DrawArgsBuffer` 校验 `startIndex + indexCount <= compactedBuffer.count`。
3. **跑断言**：用 §5 的场景 2。断言失败 → 确诊。
4. **按解 B 修**：compacted 缓冲 ×3（或多页复用一个更大缓冲），`DrawArgs` 统一到三角形单位。
5. **顺带做可见性位图**：按 cluster 槽位分配（`P0-02` 方案 c），从此与索引单位彻底解耦。

---

## 5. 建议先搭的最小验证工装

在动任何修复之前，先花半天把下面三样搭好——它们会让后面每一条修复都"可证伪"。

### 5.1 断言清单（可直接贴）

```csharp
// Authored by: <你>  用途：P1-04 / P0-02 边界校验
// 放在 VirtualMeshBakerAPI 内部（MemoryPageData 是私有嵌套类型），且必须在 WriteFiles 之前调用
// —— WriteFiles 末尾会 DisposeNativeArrays()，之后 .Length 已不可读
static void ValidateBake(List<MemoryPageData> pages, uint trianglePageStride)
{
    ulong totalTris = 0, maxTris = 0;
    foreach (var p in pages)
    {
        totalTris += p.indexValueCount;                 // 三角形数
        maxTris = Math.Max(maxTris, p.indexValueCount);
        Debug.Assert(p.vertexPositionNativeArray.Length == p.vertexValueCount);
        Debug.Assert(p.indexNativeArray.Length        == p.indexValueCount); // 1 uint / 三角形
        Debug.Assert(p.dataNativeArray.Length         == p.totalInstanceCount * 4);
        Debug.Assert(p.groupDataNativeArray.Length    == p.totalGroupCount * 4);
    }
    Debug.Assert(maxTris <= trianglePageStride, "stride 单位不一致");
    Debug.Log($"[VMesh] tris={totalTris} maxTris/page={maxTris} stride={trianglePageStride} " +
              $"compactedUintsNeeded={totalTris * 3} " +
              $"compactedUintsAllocated={(ulong)trianglePageStride * 128}");
}
```

```csharp
// 运行期：读回 DrawArgsBuffer（AsyncGPUReadback<uint>），校验不越界
void ValidateDrawArgs(uint[] args /* 每材质 5 uint: indexCount, instanceCount, startIndex, baseVertex, firstInstance */)
{
    var cap = (ulong)VirtualMeshManager.Instance.CompactedIndexBuffer.count;
    for (int i = 0; i < args.Length / 5; i++)
    {
        ulong cnt = args[i * 5 + 0], start = args[i * 5 + 2];
        Debug.Assert(start + cnt <= cap,
            $"material {i}: start({start}) + count({cnt}) > cap({cap})");
    }
}
```

```hlsl
// shader 侧：把压缩索引写入的位置打到 UAV 尾部做统计（可选）
// 或更简单——在 StoreCompactedIndices 前后用 DeviceMemoryBarrier 包一层 debug 计数
```

### 5.2 三个最小场景

| # | 场景 | 覆盖条目 | 判定方法 |
|---|---|---|---|
| 1 | **单页平面**：200 万三角形的大平面（大量 64-triangle cluster），相机固定 | `P0-02`、`P1-04`、`P2-04` | 打印每帧实际 `indexCount` 之和 vs 理论可见数；开启 §5.1 断言 |
| 2 | **多页 + 单 cascade**：把 A 场景复制 4 份拉开距离，主光 cascade 设 1 | `P0-03`、`P1-08`、`P1-01` | 阴影覆盖对比（`IsEnabled` on/off）；相机拉到 50 m 看远景是否消失 |
| 3 | **release smoke**：Build 脚本打非 Development 包 + 自动截帧 | `P0-01` | 抓帧统计 draw 次数；或用一个 `Graphics.DrawProcedural` 计数探针 |

场景 1 和 3 我建议直接进 CI（见 Phase 7）。

### 5.3 一个"缺陷可视化"开关

在 `DebugPasses.shader` 里加一个 `_VMeshDebugMode`：
- `0` 原样（哈希着色）
- `1` 只画"上一帧可见位为 1"的三角形（把 `P0-02` 的别名直接看成条纹）
- `2` 画 LOD 层（`lodType` / `instanceData.z` 的低 2 bit 染色）
- `3` 画页槽位

模式 1 是 `P0-02` 的决定性证据，做出来基本一眼定案。

---

## 6. 修复优先级与排期建议

| 批次 | 条目 | 目的 | 估计工期 | 阻断关系 |
|---|---|---|---|---|
| **A** | P0-01, P1-05, P1-08, P2-13 | 零架构风险的纯 bug，先把"release 会翻倍"和"材质>128 静默错画"堵住 | 1 天 | 无 |
| **B** | P0-03, P0-04, P0-05, P0-06, P1-01 | 正确性 + 不破坏美术资产 | 2 天 | 无 |
| **C** | §5 工装 → P0-02 + P1-04（同一专题） | 核心正确性，需要先有断言才敢改 | 半天搭工装 + 2-3 天修 | 依赖 A/B 已验证的基线 |
| **D** | P1-03, P1-06, P1-07 | 资源生命周期与 per-camera | 2 天 | P1-07 会动 shader 常量缓冲，建议在 C 之后 |
| **E** | P2-09（格式版本化） | 后面所有格式改动的地基 | 1 天 | 必须早于 P2-07 / P0-06 的支持方案 |
| **F** | P2-01, P2-02, P2-03, P2-05, P2-11 | 性能与可观测性 | 3-4 天 | 依赖 P2-11 先有数据 |
| **G** | P2-06, P2-07, P2-04 | 兑现虚拟几何体的主要收益 | 1-2 周 | 建议放到作品后期 |
| **H** | P2-08, P2-12, P3-01/02 | 管线鲁棒性 + 文档 + 测试 | 3 天 | 贯穿 |

> **给简历作品的建议**：A→B→C→D 是"可展示"的最低闭环（约 2 周），做完就有一段完整叙事（"我发现并修复了 12 个缺陷，其中 3 个只在 release 出现/需要算术推导才能定位"）。G 里的 `P2-06`（材质合并）和 `P2-07`（页压缩）才是能体现深度的地方，但排在后面。

---

## 7. 本审计未覆盖 / 未能验证

为避免误导，明确列出这次**没有**做到的事：

| 未做 | 原因 | 影响的最严重条目 |
|---|---|---|
| **编译验证** | 本机无 Unity 编辑器安装；`Library/` 与 `Library/PackageCache` 不存在，取不到 URP 17.6 / ShaderGraph 17.6 源码做签名比对 | P1-09、P3-05 |
| **运行/抓帧验证** | 同上；且工程内**尚无任何 `.vmesh` 烘焙产物**，无法构造最小场景 | P0-02、P1-03、P1-04、P1-06（全部 `[推导]` 项） |
| **DX12 / Android / iOS 行为** | 无设备、无后端。上游 README 已自认 DX12/iOS 未完成 | 平台上可能叠加新缺陷 |
| **烘焙质量评估**（简化后的视觉误差、接缝、UV 拉伸） | 需要运行 + 对比渲染 | 未列入本报告 |
| **内存峰值实测** | 需要 Memory Profiler | P1-07、P2-03 |
| **性能基准** | 需要可运行的 demo（这正是 Phase 0 的目标） | 全部 P2 性能类 |

**下一步建议**：先只做批次 A（1 天，纯收益），同时并行验证 `P1-09` 的 3 条 API 核对（这决定这个包在 6000.6 上到底能不能编译）。这两件事的结论会直接决定后面要不要继续投入。

---

*本文件为静态审计结论，所有 `[推导]` / `[待验证]` 条目请在 Phase 0 转化为可复现的观测后再作为结论对外引用。*
