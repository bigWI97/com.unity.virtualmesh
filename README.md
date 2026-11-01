# Unity Virtual Mesh Package

This package contains the sample code referenced by the "Design and Implementation of a Lightweight Virtualized Geometry System Suitable for Mobile Use" presentation during [CEDEC 2024](https://cedec.cesa.or.jp/2024/session/detail/s66051dd5da26f/) (presentation in Japanese). The video of the presentation is available on [Unity Learning Materials](https://learning.unity3d.jp/10626/).

> [!CAUTION]
> The code contained in this package is ***not*** production-ready and only serves as a reference for implementing similar virtual geometry pipelines. You can use the code freely as long as it is tied to the Unity ecosystem (for more details see the [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license)).

## Background

A typical performance issue encountered by many games is the heavy usage of CPU resources. A virtual geometry system tries to deal with this issue by moving the logic of deciding what geometry to render closer to the actual rendering itself, on the GPU. For a specific scene, all the meshes are uploaded (streamed) to the GPU and both the selection of LODs and culling of unecessary triangles are performed there right before rendering. This results in near-perfect per-material batching, LODs, and culling, all automated and without the need for tweaking assets.

This kind of system has been used in games on high-end platforms for over a decade, and now the technology and hardware present in mobile devices has caught up enough to allow us to make prototypes for other platforms as well. The goal of this package is to provide a fully customizable, C#-level implementation of a virtual geometry system that is able to showcase performance benefits even for mobile.

In order to achieve this, the virtual mesh system relies on a few concepts that we recommend understanding before diving into the package's code, including GPU instancing, compute shaders, indirect draws and dispatches, GPU-driven rendering, two-pass occlusion culling, and hierarchical level-of-details.

## Project Status

The package is currently maintained and development is still ongoing. However, since this is an experiment rather than an official Unity product, we cannot make guarantees about any kind of support or the timing of updates. Here are some of the current areas of focus and to-do tasks:

- ***Multiple-scene baking***: the system currently only works with a single scene for demonstration purposes, we are looking into extending the baking system to support multiple scenes.
- ***Baking UX improvements***: the baking system relies on an editor tool that we are looking into improving for better performance and usability.
- ***DX12 support***: support for DX12 requires some fixes to address the platform differences in how indirect draw arguments behave between APIs, and is currently underway.
- ***iOS support***: support work for iOS is currently underway.
- ***Performance optimizations***: there are still several ways to improve the system's runtime performance and time is continuously being spent on investigating how to improve performance.

## Setup

<img src="https://img.shields.io/badge/unity-6000.3.0f1-green.svg?style=flat-square" alt="unity 6000.3.0f1"> <img src="https://img.shields.io/badge/Vulkan-AC162C.svg?style=flat-square&logo=vulkan&logoColor=white" alt="Vulkan">

The package relies on URP, Vulkan, and the Render Graph system. Support is intended for LTS versions of Unity 6.3 and onwards. HDRP compatibility has not been tested.

To install the package and modify it, we recommend cloning the repository into your `Packages` folder and making sure the folder is named `com.unity.virtualmesh`. You can also import the package from the editor via the [package manager](https://docs.unity3d.com/Manual/upm-ui-actions.html).

To use the virtual mesh system, you need to follow the next few steps:

### 1. Bake virtual meshes (see *Baking* section)

### 2. Add a *VirtualMeshRenderFeature* to your Universal Renderer Data asset

![render-feature](Documentation~/images/render-feature.png)

When adding the new render feature, several shader and material assets should be set automatically. If this does not happen, you can refer to each field below:

- `Render Mode` should be set to the same value as defined by the Universal Renderer Data's `Rendering Path` setting
- `Depth Pyramid` should be set to *Packages/com.unity.virtualmesh/Runtime/RenderTextures/DepthPyramid.renderTexture*
- `Compute Passes Shader` should be set to *Packages/com.unity.virtualmesh/Runtime/Shaders/DXCVisibilityPasses.compute*
- `Pyramid Generation Passes Shader` should be set to *Packages/com.unity.virtualmesh/Runtime/Shaders/DepthPyramidPass.compute*
- `Custom Pass Material` should be set to *Packages/com.unity.virtualmesh/Runtime/Materials/CustomPassMaterial.mat*
- `Debug Pass Material` should be set to *Packages/com.unity.virtualmesh/Runtime/Materials/DebugPassMaterial.mat*

### 3. Add a *VirtualMeshManager* script to your main camera

![manager-component](Documentation~/images/manager-component.png)

The VirtualMeshManager script has several fields:

- `Copy Passes Shader` should be set to *Packages/com.unity.virtualmesh/Runtime/Shaders/CopyPasses.compute*
- `Placeholder Material` should be set to *Packages/com.unity.virtualmesh/Runtime/Materials/PlaceholderLit.mat*
- `Is Enabled` allows you to enable or disable virtual meshes without incurring the heavy cost of enabling or disabling the `Virtual Mesh Manager` component itself
- `Is Internal Draw Pass Enabled` tells the system to use a dedicated render pass to draw disoccluded geometry instead of the URP default opaque passes
- `Is Bounding Mesh Enabled` tells the system to draw an empty bounding mesh that forces URP to create shadow maps in case only virtual meshes are present in the scene
- `Is Placeholder Enabled` allows you to enable placeholder meshes that should be drawn instead of memory pages when they are not ready to be used
- `Is Debug View Enabled` allows you to render virtual meshes with a debug shader (see *Troubleshooting* section)

## Baking

Meshes that will be rendered via the virtual mesh system need to be pre-processed and saved as a separate set of custom files. We call this process baking, which can be done entirely in the editor. Once baked, the original mesh assets are not used anymore, and can be removed from the scene.

> [!NOTE]
> Only static opaque meshes are supported for now. Transparent objects can be baked, but the rendering order of those objects is not guaranteed.

Meshes that should be baked should be present in the scene as GameObjects, and collectively grouped under one single parent (the depth and structure of that parent's hierarchy does not matter). After grouping the objects for baking, follow the next steps:

1. Open the *Virtual Mesh Baker* window by selecting `Virtual Mesh` > `Open Baker` from the editor tabs.
2. Drag and drop your parent object into the `Root Game Object` field.
3. Check the `Bake Inactive Objects` checkbox if inactive GameObjects should be considered during baking.
4. Uncheck the `Bake Opaque Objects Only` checkbox if objects with render queue settings other than opaque should be baked.
5. Click on the `Full Bake` button and wait until the process completes.

When the baking completes, the console should display a message with some statistics about the number of objects that have been processed. There should also be two new folders in your project:

- *Assets/StreamingAssets/VirtualMeshData*, which contains the newly converted mesh data and their materials, saved into AssetBundles.
- *Assets/VirtualMeshCache*, which contains intermediate assets that are generated by the baking process and saved here for debugging.

> [!CAUTION]
> The baking system is configured to build asset bundles for the currently selected build platform. To visualize virtual meshes in the editor, make sure that you have performed a full bake with Vulkan as Graphics API and Windows selected as your active build profile.

## Technical Details

See the [Glossary](Documentation~/glossary.md) section for a list of vocabulary and concepts that are frequently used in the package.

See the [Implementation](Documentation~/implementation.md) section for details about the package's architecture and implementation details.

## Feature Support

Here is a list of Unity features that require specific care because they may not be compatible with this package by default.

| Feature | Support Status |
| --- | --- |
| Directional Light | Both lighting and shadows are supported up to 4 cascades. |
| Additional Lights | Only lighting is supported (no shadows). |
| Light Baking | Only one set of UVs is supported for lightmaps. |
| Rendering Path | Both Deferred and Forward are supported. |
| Post Effects | Post-processing effects that require prepasses that render to the GBuffer are not supported. |

## Troubleshooting

To check if the virtual system is correctly rendering objects, you can enable the Unity Frame Debugger and check if passes such as *VirtualMeshRenderFeature Draw Shadow Pass* or *VirtualMeshRenderFeature Refresh Depth Pyramid Pass* are actively drawing content for shadows and opaque geometry respectively:

![frame-debugger](Documentation~/images/frame-debugger.png)

The VirtualMeshManager component includes a checkbox to activate a debug shader, called *Is Debug View Enabled*. This replaces the first pass of the two-pass occlusion culling algorithm with a debug shader render that relies on *Runtime/Shaders/DebugPasses.shader*, which by default renders virtual meshes with a transparent blend based on camera depth to visualize overdraw and check if culling is working:

![debug-view](Documentation~/images/debug-view.png)

The shader also contains commented code to switch to a triangle view to see if LOD-switching is working correctly:

![triangle-view](Documentation~/images/triangle-view.png)

## License

See [LICENSE.md](LICENSE.md).

## 修改文档

### 2026.11.01

#### 配Vulkan

Player Settings → Other Settings → Graphics APIs 只留 Vulkan，删掉 D3D12/D3D11。Active Build Target = Windows。
原因：README L85-86 明确写了烘焙对当前 active build target 打 AB，而运行期第 0 帧就要 AssetBundle.LoadFromFile。另外 DrawProceduralIndirect + GraphicsBuffer.Target.Index + startIndex 这套在 DX12 下有已知问题（README L22 自己列了）。

#### Stage 0 测试场景

目标：不动 OasisScene，先用 Tent_01_Prefab + Teleporter_Platform_01_Prefab 两个 prefab 验证 `ShaderGraph → *_VMESH.shader → 出图` 这条链路。Oasis 的材质几乎全是 ShaderGraph，这条链路不通的话整个场景烘出来就是空的。
MCP（unity）一开始能读到活动场景 OasisScene / Edit 模式，但 `create_scene` 超时后所有调用（`get_scene_info`、`get_play_mode_status`）都超时，判断 Unity 主线程被阻塞（大概率弹了"保存场景"模态框）；已核对磁盘，`Assets/Scenes/Oasis/` 下没有生成 `VMeshTest_00_ShaderGraph.unity`，无半成品。
改用不依赖 MCP 的确定性方案：新增 [Editor/VMeshStage0Builder.cs](Editor/VMeshStage0Builder.cs)，菜单 `Virtual Mesh / Tests / Build Stage 0 Scene` 一键建场景、建 VMRoot、实例化两个 prefab、给主相机挂 VirtualMeshManager 并回填 `m_CopyPassesShader` / `m_PlaceholderMaterial`、把 `m_CameraLoadDistanceThreshold` 设为 300，然后保存。脚本用 `SerializedObject` 写字段而不是调用 `CameraLoadDistanceThreshold` 属性——该属性 setter 会写 `m_PageStrideConstants`，而它只在运行时 `AllocateData()` 里创建，编辑器里调用会抛 NullReferenceException。
待办：① 执行该菜单；② 手动把 VirtualMeshRenderFeature 加到当前 Quality Level 用的 Renderer Data，Render Mode 与该 renderer 的 Rendering Path 一致；③ `Bake Shaders Only` 后检查 `Assets/VirtualMeshCache/Shaders/Tent_Graph_VMESH.shader` 是否包含 `com.unity.virtualmesh/Runtime/ShaderLibrary/PBRForwardPass.hlsl`，不含即说明 ShaderGraph 的 include 替换没命中，先修 `VirtualMeshBakerAPI.ConvertShaders` 的替换表再往下走。

关闭保存弹窗后重试：`execute_menu_item` 一开始报"菜单项不存在"，原因是新加的 .cs 还没被 AssetDatabase 导入（磁盘上没有 `.meta`）；执行 `Assets/Refresh` 后生成 meta，再 `recompile_scripts` 才注册成功（0 error）。最终菜单执行成功，场景已生成并保存，校验通过：`VMRoot` 下 `Tent_01` + `Teleporter_Platform_01` 两个 prefab 实例；`Main Camera` 上 `VirtualMeshManager` 的 `m_CopyPassesShader`、`m_PlaceholderMaterial`、`m_CameraLoadDistanceThreshold: 300` 三个字段都已持久化进场景文件。

过程中发现并修掉一个问题：`m_CameraLoadDistanceThreshold` 原本**没有 `[SerializeField]`**，所以 implementation.md 说它"可调"是错的——既不能在 Inspector 里改，也无法持久化，每次 domain reload 都回到 20。已给该字段补上 `[SerializeField]` 并在注释里说明。同时更正一处先前的误判：Oasis 的 prefab **是有 LODGroup 的**（`Tent_01_Mesh`、`Teleporter_Platform_01_Mesh` 上都带），之前只 grep 场景 YAML 得出"0 个 LODGroup"是错的——LODGroup 在 prefab 内，场景文件只存 PrefabInstance 的覆盖值。

② `VirtualMeshRenderFeature` 加到 当前URP管线的RendererFeature；③ `Bake Shaders Only` → 检查 `Tent_Graph_VMESH.shader` 的 include 是否被替换成 `Packages/com.unity.virtualmesh/Runtime/ShaderLibrary/PBRForwardPass.hlsl`。

#### Stage 0 Full Bake 结果（2 个物体）

Console 统计：`Serialized 389 instances into 1 pages (min: 0, max: 389) from 3 submeshes (degenerative cluster ratio: 0.0925, average leaf cluster ratio: 0.15, max vertex values: 21428, max index values: 23884, max group count: 55, material count: 3)`。
解读：389 = 所有 LOD 层的 cluster 总数，`max group count 55` = 55 个 cluster group；只填了 1 页（远低于 256 上限）；3 个 submesh、3 个唯一材质；9.25% 的 cluster 不满 64 三角形属正常。**`max index values: 23884` ≈ 页内三角形数**，实证了审计报告 P1-04 的前提——`IndexValuePageStride` 的单位是「三角形」而不是「索引值」。

#### ShaderGraph 转换验证通过

生成了 `Assets/VirtualMeshCache/Shaders/Tent_Graph_VMESH.shader` 与 `Lit_VMESH.shader`，6 个 PBR include 全部替换成功（`PBRForwardPass` L639、`PBRGBufferPass` L1253、`PBRShadowCasterPass` L1577、`PBRDepthOnlyPass` L2200、`PBRDepthNormalsOnlyPass` L2555、`PBR2DPass` L4047）；`Lit_VMESH.shader` 的 6 个 Lit*Pass 同样全部替换。
更关键的是确认了这是**真替换**而不是只改路径——6 个 PBR pass 的顶点入口都变成了 VMESH 版本 `PackedVaryings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)`（行号 80/76/6/6/6/3）。残留的 `Editor/ShaderGraph/Includes/*` 只剩 `ShaderPass.hlsl`、`Varyings.hlsl`（必需）加 MotionVector/Meta/SelectionPicking（本来就不在替换表内），属预期。
结论：**ShaderGraph 17.6 的生成格式与 `ConvertShaders` 的替换表完全匹配**，这条最脆的链路通了。

#### 输出资源路径

运行期数据在 `Assets/StreamingAssets/VirtualMeshData/`：`metadata.vmesh`（全局元数据）、`NNNNNNNN.vmesh` × 256（每页 7 个 uint 的页头）、`NNNNNNNN.vmeshdata` × 256（页数据）、`Materials/virtualmeshmaterials`(+manifest)、`Placeholders/virtualmeshplaceholders`(+manifest) 两个 AssetBundle。注意 256 个页文件是**无条件全写**（`WriteFiles` 循环 0..255），只有第 1 页有内容，其余是 0 字节空壳——by design，不是 bug。
中间产物在 `Assets/VirtualMeshCache/`：`Shaders/`（2 个，关键诊断物）、`Materials/`（`0/1/2.mat` + `MaterialCollection.asset`）、`Meshes/`（256 个页级占位 Mesh + `MeshCollection.asset`）、`Obj/`（`k_ExportOBJ = false`，空）。

#### 渲染链路确认

`m_CurrentQuality: 3` → PC High → `PC_High.asset`(guid `4b83569d…`) → `m_RendererDataList[0]` = `PC_High_Renderer.asset`(guid `f288ae1f…`)，`VirtualMeshRenderFeature` 已挂在该资产上（`m_Active: 1`，5 个资源引用全部自动回填），相机 `actualRenderingPath = Forward`。整条链是通的。
一个语义陷阱：该 feature 的 `renderMode` 序列化值是 **3**，而代码只在 `== RenderingPath.DeferredShading`(=2) 时才走 GBuffer 分支，值 3 会落到 else 分支（用 `cameraColor + cameraDepth`）——对当前 Forward 路径**恰好正确**，但将来切 Deferred 必须把这个值明确改成 `DeferredShading`，否则会静默走 Forward 分支。见 `VirtualMeshRenderFeature.cs:343,607`。

#### 内存：约 30 倍超额分配

Stage 0 实际几何约 1.2 万三角形 / 0.6 MB，但 GPU 缓冲按「页步长 × 可加载页数」分配：VertexPosition ≈2.6 MB、VertexAttribute ≈5.2 MB、Index ≈2.9 MB、CompactedIndex ≈6–15 MB（随 cascade 数），合计 **约 19–28 MB**。原因见 `VirtualMeshManager.cs:1001-1041`——`m_LoadableMemoryPageCount` 只按页数向上取整钳到 32，完全不知道真实内容量。Stage 0 无所谓，加内容时需关注。

#### 流式淘汰的门槛（重要）

页数会先向上取整到 32/64/128/256，然后 `if (m_MemoryPageCount < m_LoadableMemoryPageCount) m_LoadableMemoryPageCount = m_MemoryPageCount;`（`VirtualMeshManager.cs:794-806`）。推论：页数 1–32 → 槽位 32；33–64 → 64；65–128 → 128，三种情况槽位都 ≥ 页数，**永远不会换出**；只有 **>128 页**（约 400–800 万三角形）才会触发页淘汰。也就是说 5 态机里的 `Waiting`/`TooFar`/`Loading` 淘汰路径在 Stage 0/1/2 都跑不到，要验证虚拟内存分页必须直接上万级页数或全 Oasis。

#### LOD 验证场景 + 统计 HUD

新增 [Runtime/VMeshStats.cs](Runtime/VMeshStats.cs)（纯新增，`Unity.VirtualMesh.Runtime` 命名空间，`AddComponentMenu("Virtual Mesh/VMesh Stats HUD")`），挂到主相机。每 `SampleInterval`(默认 15) 帧用 `AsyncGPUReadback` 读回两个缓冲：`DrawArgsBuffer` 每材质的 `indexCount/3` 求和得到**本帧真正提交的三角形数**，`DispatchArgsBuffer[0]` 得到通过 cluster 级剔除的 cluster 数；连同材质数、页数、cluster 容量一起画在 y=78/108（避开 `VirtualMeshManager.OnGUI` 在 y=10/40 的页状态显示）。这是"剔除是否真的生效"的硬指标。

场景侧把 `Tent_01` 复制 8 份为 `Tent_01_D1..D8`，x 固定 -5，z = 20/45/75/110/155/210/280/360（距相机 22–382 m），形成一条纵深序列用于观察 LOD 阶梯。复制件保留原 Renderer 的启用状态**不影响烘焙**——`GetFilterList` 是按 GameObject 的 active 状态取 `MeshFilter`，不检查 Renderer 组件是否 enabled。
另外，10 个帐篷的 LOD 子物体同名（都叫 `Tent_01_LOD0/1/2`），`GetFilterList` 第二段循环是按**名字**判断过滤器是否已归属某个 LODGroup，因此这些过滤器会被全部跳过——但第一段循环已经从每个 LODGroup 取到了 LOD0，最终仍是 11 个 submesh，**结果正确**。这正是审计报告里"按名字匹配"脆弱点在当前内容下的巧合性成立。

#### 阶段总结

1.这个包只是把场景烘焙出nanite数据放到StreamingAssets验证流程的，离落地差太远。需要改成按照mesh烘焙VM数据，场景中可拖放，BRG绘制等操作。