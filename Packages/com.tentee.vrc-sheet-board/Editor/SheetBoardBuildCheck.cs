#if UNITY_EDITOR
using System;
using System.Collections.Generic;
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
            List<string> errors = new List<string>();
            List<SheetBoardSource> errorSources = new List<SheetBoardSource>();
            for (int i = 0; i < roots.Length; i++)
            {
                SheetBoardSource[] sources = roots[i].GetComponentsInChildren<SheetBoardSource>(true);
                for (int j = 0; j < sources.Length; j++)
                {
                    SheetBoardSource source = sources[j];
                    if (source == null || !source.gameObject.activeInHierarchy || !source.enabled) continue;

                    string sourceName = source.gameObject.name;
                    bool hasError = false;
                    if (!HasValidUrl(source))
                    {
                        errors.Add("SheetBoard: Source「" + sourceName + "」に URL が設定されていません。Inspector で公開URLを貼り、「URL を適用」を押してください。");
                        hasError = true;
                    }
                    if (!string.IsNullOrWhiteSpace(source.baseUrl) && !SheetBoardSourceEditor.UrlsMatch(source))
                    {
                        errors.Add("SheetBoard: Source「" + sourceName + "」の公開URLが適用されていません。Inspector の「URL を適用」を押してから公開してください。");
                        hasError = true;
                    }

                    string permissionError = ValidateSource(source);
                    if (permissionError != null)
                    {
                        errors.Add("SheetBoard: Source「" + sourceName + "」" + permissionError);
                        hasError = true;
                    }
                    if (hasError) errorSources.Add(source);
                }

                SheetBoardDisplay[] displays = roots[i].GetComponentsInChildren<SheetBoardDisplay>(true);
                for (int j = 0; j < displays.Length; j++)
                {
                    SheetBoardDisplay display = displays[j];
                    if (display == null || !display.gameObject.activeInHierarchy || !display.enabled || display.source != null) continue;
                    Debug.LogWarning("SheetBoard: Display「" + display.gameObject.name + "」の Source が設定されていません。", display);
                }
            }

            if (errors.Count == 0) return;
            string message = string.Join("\n", errors.ToArray());
            if (report != null) throw new BuildFailedException(message);
            Debug.LogError(message, errorSources[0]);
        }

        private static bool HasValidUrl(SheetBoardSource source)
        {
            if (source.urls == null) return false;
            for (int i = 0; i < source.urls.Length; i++)
                if (source.urls[i] != null && !string.IsNullOrEmpty(source.urls[i].Get())) return true;
            return false;
        }

        private static string ValidateSource(SheetBoardSource source)
        {
            if (source == null || source.audience == SheetBoardAudience.Public) return null;
            UdonBehaviour controller = source.permissionController;
            if (!IsValidController(source, controller))
                return "は制限付きで、同じシーンの有効な対応版 PermissionController がありません。";
            return null;
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
