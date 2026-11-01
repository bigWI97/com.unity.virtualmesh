#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Unity.VirtualMesh.Editor
{
    /// <summary>
    /// Added for the Stage 0 pipeline smoke test. NOT part of the upstream com.unity.virtualmesh package.
    ///
    /// Builds a minimal scene (1 tent + 1 teleporter platform under a "VMRoot" parent) that is used to
    /// validate the "ShaderGraph -> *_VMESH.shader -> draw" path before touching the real Oasis scene.
    /// The point is to surface a failed ShaderGraph include replacement within minutes instead of after
    /// a full bake.
    ///
    /// Menu: Virtual Mesh / Tests / Build Stage 0 Scene
    /// </summary>
    public static class VMeshStage0Builder
    {
        private const string k_ScenePath = "Assets/Scenes/Oasis/VMeshTest_00_ShaderGraph.unity";

        // Art/Architecture/Prefabs/Tent_01_Prefab.prefab
        private const string k_TentGuid = "74aeec6002edd31428364ee5a1c44741";
        // Art/Architecture/Prefabs/Teleporter_Platform_01_Prefab.prefab
        private const string k_PlatformGuid = "fd9ac525a3915e7448da57d18ace561f";

        private const string k_CopyPassesShaderPath = "Packages/com.unity.virtualmesh/Runtime/Shaders/CopyPasses.compute";
        private const string k_PlaceholderMaterialPath = "Packages/com.unity.virtualmesh/Runtime/Materials/PlaceholderLit.mat";

        [MenuItem("Virtual Mesh/Tests/Build Stage 0 Scene")]
        public static void Build()
        {
            // Explicitly ask first: switching scenes unloads whatever is currently open.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var root = new GameObject("VMRoot");

            Spawn(k_TentGuid, "Tent_01", root.transform,
                new Vector3(-5f, 0f, 0f), Quaternion.Euler(0f, 180f, 0f));
            Spawn(k_PlatformGuid, "Teleporter_Platform_01", root.transform,
                new Vector3(5f, 0f, 0f), Quaternion.identity);

            var camera = Camera.main;
            if (camera == null)
            {
                Debug.LogError("[VMesh Stage0] The new scene has no Main Camera (expected NewSceneSetup.DefaultGameObjects).");
            }
            else
            {
                camera.transform.SetPositionAndRotation(new Vector3(0f, 8f, -22f), Quaternion.Euler(14f, 0f, 0f));
                SetupManager(camera.gameObject);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, k_ScenePath))
            {
                Debug.LogError($"[VMesh Stage0] Failed to save scene to {k_ScenePath}");
                return;
            }

            AssetDatabase.Refresh();

            Debug.Log(
                "[VMesh Stage0] Scene built: " + k_ScenePath + "\n" +
                "NEXT (manual, in this order):\n" +
                "  1) Add 'VirtualMeshRenderFeature' to the Renderer Data used by the active Quality Level,\n" +
                "     and set its Render Mode to match that renderer's Rendering Path.\n" +
                "  2) Virtual Mesh > Open Baker -> Root Object = VMRoot -> click 'Bake Shaders Only'.\n" +
                "  3) Open Assets/VirtualMeshCache/Shaders/Tent_Graph_VMESH.shader and confirm it contains\n" +
                "     'com.unity.virtualmesh/Runtime/ShaderLibrary/PBRForwardPass.hlsl'.\n" +
                "     If it does not, the ShaderGraph include replacement missed: update the Replace() table\n" +
                "     in VirtualMeshBakerAPI.ConvertShaders before doing anything else.\n" +
                "  4) Only then: Full Bake, disable the VMRoot renderers, enter Play mode.");
        }

        private static void Spawn(string guid, string name, Transform parent, Vector3 position, Quaternion rotation)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError($"[VMesh Stage0] GUID not found for '{name}': {guid}");
                return;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError($"[VMesh Stage0] Failed to load prefab '{name}': {path}");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.name = name;
            instance.transform.SetPositionAndRotation(position, rotation);
        }

        private static void SetupManager(GameObject cameraGo)
        {
            var manager = cameraGo.GetComponent<Runtime.VirtualMeshManager>();
            if (manager == null)
                manager = cameraGo.AddComponent<Runtime.VirtualMeshManager>();

            if (!TryResolveSerialized(manager, out var so))
                return;

            SetObject(so, "m_CopyPassesShader",
                AssetDatabase.LoadAssetAtPath<ComputeShader>(k_CopyPassesShaderPath), k_CopyPassesShaderPath);
            SetObject(so, "m_PlaceholderMaterial",
                AssetDatabase.LoadAssetAtPath<Material>(k_PlaceholderMaterialPath), k_PlaceholderMaterialPath);

            // IMPORTANT: do NOT call the CameraLoadDistanceThreshold property here. Its setter writes into
            // m_PageStrideConstants, which is only created in AllocateData() at runtime (OnEnable).
            // Calling it in edit mode throws NullReferenceException. Set the backing field instead.
            var distance = so.FindProperty("m_CameraLoadDistanceThreshold");
            if (distance != null)
                distance.floatValue = 300f;
            else
                Debug.LogWarning("[VMesh Stage0] Serialized field 'm_CameraLoadDistanceThreshold' not found.");

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);
        }

        private static bool TryResolveSerialized(Object target, out SerializedObject so)
        {
            so = new SerializedObject(target);
            if (so.targetObject == null)
            {
                Debug.LogError("[VMesh Stage0] Could not create SerializedObject for VirtualMeshManager.");
                return false;
            }

            return true;
        }

        private static void SetObject(SerializedObject so, string fieldName, Object value, string assetPath)
        {
            var prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                Debug.LogWarning($"[VMesh Stage0] Serialized field '{fieldName}' not found on VirtualMeshManager.");
                return;
            }

            if (value == null)
            {
                Debug.LogError($"[VMesh Stage0] Asset not found: {assetPath}");
                return;
            }

            prop.objectReferenceValue = value;
        }
    }
}
#endif
