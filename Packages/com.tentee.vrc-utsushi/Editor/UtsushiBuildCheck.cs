#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using Utsushi;
using UdonSharpEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Udon;

namespace UtsushiEditor
{
    public sealed class UtsushiBuildCheck : IProcessSceneWithReport
    {
        private static readonly System.Collections.Generic.Dictionary<Type, bool> CompatibilityCache = new System.Collections.Generic.Dictionary<Type, bool>();
        public int callbackOrder => -1000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            List<string> errors = new List<string>();
            List<UtsushiSource> errorSources = new List<UtsushiSource>();
            for (int i = 0; i < roots.Length; i++)
            {
                UtsushiSource[] sources = roots[i].GetComponentsInChildren<UtsushiSource>(true);
                for (int j = 0; j < sources.Length; j++)
                {
                    UtsushiSource source = sources[j];
                    if (source == null || !source.gameObject.activeInHierarchy || !source.enabled) continue;

                    string sourceName = source.gameObject.name;
                    bool hasError = false;
                    if (!HasValidUrl(source))
                    {
                        errors.Add("Utsushi: データ元「" + sourceName + "」に掲示板URLが設定されていません。Inspector の「掲示板URL」に、シートの管理者から受け取った URL を貼ってください。");
                        hasError = true;
                    }
                    if (!string.IsNullOrWhiteSpace(source.baseUrl) && !UtsushiSourceEditor.UrlsMatch(source))
                    {
                        errors.Add("Utsushi: データ元「" + sourceName + "」の掲示板URLを反映できませんでした。" + UtsushiInstaller.ProgramFailureMessage());
                        hasError = true;
                    }

                    string permissionError = ValidateSource(source);
                    if (permissionError != null)
                    {
                        errors.Add("Utsushi: データ元「" + sourceName + "」" + permissionError);
                        hasError = true;
                    }
                    if (hasError) errorSources.Add(source);
                }

                UtsushiDisplay[] displays = roots[i].GetComponentsInChildren<UtsushiDisplay>(true);
                for (int j = 0; j < displays.Length; j++)
                {
                    UtsushiDisplay display = displays[j];
                    if (display == null || !display.gameObject.activeInHierarchy || !display.enabled || display.source != null) continue;
                    Debug.LogWarning("Utsushi: 掲示板「" + display.gameObject.name + "」のデータ元が設定されていません。", display);
                }
            }

            if (errors.Count == 0) return;
            string message = string.Join("\n", errors.ToArray());
            if (report != null) throw new BuildFailedException(message);
            Debug.LogError(message, errorSources[0]);
        }

        private static bool HasValidUrl(UtsushiSource source)
        {
            if (string.IsNullOrWhiteSpace(source.baseUrl)) return false;
            if (source.urls == null) return false;
            for (int i = 0; i < source.urls.Length; i++)
                if (source.urls[i] != null && !string.IsNullOrEmpty(source.urls[i].Get())) return true;
            return false;
        }

        private static string ValidateSource(UtsushiSource source)
        {
            if (source == null || source.audience == UtsushiAudience.Public) return null;
            UdonBehaviour controller = source.permissionController;
            if (!IsValidController(source, controller))
                return "は閲覧を制限していますが、同じシーンに有効な PermissionController がありません。";
            return null;
        }

        internal static bool IsValidController(UtsushiSource source, UdonBehaviour controller)
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
