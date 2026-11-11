#if UNITY_EDITOR
using SheetBoard;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDKBase.Editor.BuildPipeline;

namespace SheetBoardEditor
{
    public sealed class SheetBoardBuildPrepare : IVRCSDKBuildRequestedCallback
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
                    SheetBoardSource[] sources = roots[rootIndex].GetComponentsInChildren<SheetBoardSource>(true);
                    for (int i = 0; i < sources.Length; i++)
                    {
                        SheetBoardSource source = sources[i];
                        if (source == null || !source.enabled || !source.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(source.baseUrl)) continue;
                        if (!SheetBoardSourceEditor.UrlsMatch(source)) SheetBoardSourceEditor.EnsureUrlsApplied(source);
                    }
                }
            }
            return true;
        }
    }
}
#endif
