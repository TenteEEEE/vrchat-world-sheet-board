#if UNITY_EDITOR
using System;
using System.Reflection;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;

namespace SheetBoardEditor
{
    public sealed class SheetBoardBuildCheck : IProcessSceneWithReport
    {
        private static readonly System.Collections.Generic.Dictionary<Type, bool> CompatibilityCache = new System.Collections.Generic.Dictionary<Type, bool>();
        public int callbackOrder => -1000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            const string urlWarning = "SheetBoard: baseUrl と生成済み URL が一致しません。URL を適用してから公開してください。";
            for (int i = 0; i < roots.Length; i++)
            {
                SheetBoardSource[] sources = roots[i].GetComponentsInChildren<SheetBoardSource>(true);
                for (int j = 0; j < sources.Length; j++)
                {
                    ValidateSource(sources[j], report);
                    if (sources[j] != null && !string.IsNullOrWhiteSpace(sources[j].baseUrl) && !SheetBoardSourceEditor.UrlsMatch(sources[j]))
                        Debug.LogWarning(urlWarning, sources[j]);
                }
            }
        }

        private static void ValidateSource(SheetBoardSource source, BuildReport report)
        {
            if (source == null || source.audience == SheetBoardAudience.Public) return;
            UdonBehaviour controller = source.permissionController;
            if (!IsValidController(source, controller))
            {
                const string message = "SheetBoard: 制限付き Source に同じシーンの有効な対応版 PermissionController がありません。";
                if (report != null) throw new BuildFailedException(message);
                Debug.LogError(message, source);
            }
        }

        internal static bool IsValidController(SheetBoardSource source, UdonBehaviour controller)
        {
            if (source == null || controller == null || !controller.enabled || !controller.gameObject.activeInHierarchy) return false;
            Scene sourceScene = source.gameObject.scene;
            Scene controllerScene = controller.gameObject.scene;
            if (!sourceScene.IsValid() || !sourceScene.isLoaded || !controllerScene.IsValid() || !controllerScene.isLoaded || controllerScene != sourceScene) return false;

            MonoBehaviour proxy = UdonSharpEditorUtility.GetProxyBehaviour(controller);
            if (proxy == null || !proxy.enabled || proxy.GetType().Name != "PermissionController") return false;
            return HasCompatiblePlayerObject(proxy.GetType());
        }

        private static bool HasCompatiblePlayerObject(Type controllerProxyType)
        {
            if (controllerProxyType == null) return false;
            if (CompatibilityCache.TryGetValue(controllerProxyType, out bool cached)) return cached;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try { types = assemblies[a].GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                for (int i = 0; i < types.Length; i++)
                {
                    Type playerObjectType = types[i];
                    if (playerObjectType == null || playerObjectType == controllerProxyType) continue;
                    FieldInfo controller = playerObjectType.GetField("Controller", flags);
                    FieldInfo permission = playerObjectType.GetField("CurrentPermission", flags);
                    FieldInfo initialized = playerObjectType.GetField("IsInitialized", flags);
                    if (controller == null || controller.FieldType != controllerProxyType || permission == null || !permission.FieldType.IsEnum ||
                        Enum.GetUnderlyingType(permission.FieldType) != typeof(int) || initialized == null || initialized.FieldType != typeof(bool)) continue;
                    CompatibilityCache[controllerProxyType] = true;
                    return true;
                }
            }
            CompatibilityCache[controllerProxyType] = false;
            return false;
        }
    }
}
#endif
