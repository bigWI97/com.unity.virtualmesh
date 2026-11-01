using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.VirtualMesh.Runtime
{
    /// <summary>
    /// Debug HUD added for the Stage 0/1 verification of the virtual mesh pipeline.
    /// NOT part of the upstream com.unity.virtualmesh package.
    ///
    /// The interesting number is the per-frame *visible triangle count*: it is read back straight from the
    /// indirect draw arguments the GPU produced, so it is the culled result, not the source geometry.
    ///
    /// IMPORTANT: VirtualMeshManager creates DrawArgsBuffer with
    ///     new GraphicsBuffer(Target.IndirectArguments, materialCount, IndirectDrawIndexedArgs.size)
    /// i.e. count = materialCount and stride = 20 bytes. AsyncGPUReadback.GetData&lt;T&gt;() sizes the returned
    /// NativeArray by the buffer's *element count*, not by byteSize / sizeof(T), so a plain
    /// Request(buffer, callback) only yields `materialCount` uints (3 for this scene) and every per-material
    /// read at i*5 falls outside the array. That silently produced "visible tris: 0" in the first version.
    /// The fix is the explicit byte-range overload: Request(buffer, sizeInBytes, offsetInBytes, callback).
    /// </summary>
    [AddComponentMenu("Virtual Mesh/VMesh Stats HUD")]
    public class VMeshStats : MonoBehaviour
    {
        private const int k_UintsPerDrawArgs = 5;      // indexCount, instanceCount, startIndex, baseVertex, firstInstance
        private const int k_BytesPerDrawArgs = k_UintsPerDrawArgs * sizeof(uint);

        [Tooltip("Number of frames between GPU readbacks. Higher = less stutter, slower HUD.")]
        [Min(1)]
        public int SampleInterval = 15;

        [Tooltip("Draw the HUD in the game view / editor play mode.")]
        public bool ShowOnScreen = true;

        private bool m_InFlight;
        private bool m_ReadbackError;
        private bool m_LayoutWarning;
        private int m_FramesSinceSample;
        private int m_MaterialCount;
        private ulong m_VisibleTriangles;
        private uint m_VisibleClusters;
        private uint[] m_PerMaterialIndexCount = new uint[0];
        private GUIStyle m_Style;

        // Sampling happens in LateUpdate so the request is submitted after the frame's dispatch work
        // has been recorded; sampling in Update can capture the buffer right after InitArgumentPass
        // zeroed it, which is another source of misleading zeros.
        private void LateUpdate()
        {
            var manager = VirtualMeshManager.Instance;
            if (manager == null || !manager.IsInitialized)
                return;
            if (manager.DrawArgsBuffer == null || manager.DispatchArgsBuffer == null)
                return;

            m_FramesSinceSample++;
            if (m_InFlight || m_FramesSinceSample < SampleInterval)
                return;

            m_FramesSinceSample = 0;
            m_MaterialCount = manager.Materials.Length;
            if (m_MaterialCount <= 0)
                return;

            m_InFlight = true;

            // Explicit byte range: materialCount * 20 bytes -> NativeArray<uint> of materialCount * 5.
            AsyncGPUReadback.Request(manager.DrawArgsBuffer, m_MaterialCount * k_BytesPerDrawArgs, 0, drawRequest =>
            {
                if (drawRequest.hasError)
                {
                    m_ReadbackError = true;
                    m_InFlight = false;
                    return;
                }

                var arguments = drawRequest.GetData<uint>();
                int perMaterial = Mathf.Min(arguments.Length / k_UintsPerDrawArgs, m_MaterialCount);

                if (perMaterial <= 0)
                {
                    // Never report 0 triangles in this case: 0 would be indistinguishable from "fully culled".
                    m_LayoutWarning = true;
                    m_InFlight = false;
                    return;
                }

                m_LayoutWarning = false;
                if (m_PerMaterialIndexCount.Length < perMaterial)
                    m_PerMaterialIndexCount = new uint[perMaterial];

                ulong triangles = 0;
                for (int i = 0; i < perMaterial; i++)
                {
                    uint indexCount = arguments[i * k_UintsPerDrawArgs];
                    m_PerMaterialIndexCount[i] = indexCount;
                    triangles += indexCount / 3ul;
                }

                m_VisibleTriangles = triangles;

                // DispatchArgsBuffer is a Raw uint buffer, so count == uint count and a plain request is fine.
                AsyncGPUReadback.Request(manager.DispatchArgsBuffer, sizeof(uint), 0, dispatchRequest =>
                {
                    m_InFlight = false;
                    if (dispatchRequest.hasError)
                    {
                        m_ReadbackError = true;
                        return;
                    }

                    var clusters = dispatchRequest.GetData<uint>();
                    m_VisibleClusters = clusters.Length > 0 ? clusters[0] : 0u;
                });
            });
        }

        private void OnGUI()
        {
            if (!ShowOnScreen)
                return;

            var manager = VirtualMeshManager.Instance;
            if (manager == null || !manager.IsInitialized)
                return;

            if (m_Style == null)
            {
                m_Style = new GUIStyle(GUI.skin.label) { fontSize = 22 };
                m_Style.normal.textColor = Color.white;
            }

            // VirtualMeshManager draws its own page-status HUD at y = 10 / 40, so stay below it.
            string triangles = m_LayoutWarning ? "n/a (unexpected readback layout)"
                             : m_ReadbackError ? "n/a (readback failed)"
                             : $"{m_VisibleTriangles:N0}";

            GUI.Label(new Rect(10, 78, 1500, 30),
                $"visible tris: {triangles}   clusters: {m_VisibleClusters:N0} / {manager.TotalInstanceCount:N0}", m_Style);

            GUI.Label(new Rect(10, 108, 1500, 30),
                $"materials: {m_MaterialCount}   pages: {manager.TotalPageCount}   sample: every {SampleInterval}f (LateUpdate)", m_Style);

            // Per-material index counts make it obvious whether a material is silently drawing nothing.
            int shown = Mathf.Min(m_PerMaterialIndexCount.Length, 6);
            if (shown > 0 && !m_LayoutWarning && !m_ReadbackError)
            {
                var sb = new System.Text.StringBuilder("indexCount per material: ");
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append("  ");
                    sb.Append(i).Append('=').Append(m_PerMaterialIndexCount[i]);
                }
                if (m_MaterialCount > shown) sb.Append("  …");
                GUI.Label(new Rect(10, 138, 1500, 30), sb.ToString(), m_Style);
            }
        }
    }
}
