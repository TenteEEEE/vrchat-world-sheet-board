#if UNITY_EDITOR
using Utsushi;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;

namespace UtsushiEditor
{
    public sealed class UtsushiBuildPrepare : IVRCSDKBuildRequestedCallback
    {
        public int callbackOrder => -1000;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                GameObject[] roots = scene.GetRootGameObjects();
                for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
                {
                    UtsushiSource[] sources = roots[rootIndex].GetComponentsInChildren<UtsushiSource>(true);
                    for (int i = 0; i < sources.Length; i++)
                    {
                        UtsushiSource source = sources[i];
                        if (source == null || !source.enabled || !source.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(source.baseUrl)) continue;
                        if (!UtsushiSourceEditor.UrlsMatch(source)) UtsushiSourceEditor.EnsureUrlsApplied(source);
                    }
                }
            }
            return true;
        }
    }
}
#endif
