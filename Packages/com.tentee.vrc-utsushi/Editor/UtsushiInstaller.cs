#if UNITY_EDITOR
using Utsushi;
using TMPro;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using VRC.Udon;

namespace UtsushiEditor
{
    public static partial class UtsushiInstaller
    {
        internal static string RootDir
        {
            get
            {
                string path = AssetDatabase.GUIDToAssetPath("f25bda302609d324ca7ec310a3b38160");
                if (!string.IsNullOrEmpty(path)) return path.Substring(0, path.LastIndexOf('/') + 1);
                return AssetDatabase.IsValidFolder("Packages/com.tentee.vrc-utsushi") ? "Packages/com.tentee.vrc-utsushi/" : "Assets/Utsushi/";
            }
        }
        private static string SourcePath => RootDir + "Runtime/UtsushiSource";
        private static string DisplayPath => RootDir + "Runtime/UtsushiDisplay";
        private static string FontPath => RootDir + "Fonts/Utsushi JP Dynamic.asset";
        internal static string SourcePrefab => RootDir + "Prefabs/Utsushi Source.prefab";
        internal static string DisplayPrefab => RootDir + "Prefabs/Utsushi Display.prefab";
        internal static string DisplayDarkPrefab => RootDir + "Prefabs/Utsushi Display Dark.prefab";
        private const string SourceTemp = "Utsushi Source (Build Temp)";
        private const string DisplayTemp = "Utsushi Display (Build Temp)";
        private const float BoldWeight = 1.25f;

        internal static void BuildPrefabs()
        {
            EnsureProgramAssets();
            TMP_FontAsset font = EnsureFontAsset();
            EnsureDirectories();
            Material ui = EnsureUiMaterial();
            Material tmp = EnsureTmpMaterial(font);
            DestroyLeftoverBuildRoot(SourceTemp);
            GameObject sourceRoot = new GameObject(SourceTemp);
            try
            {
                UtsushiSource source = sourceRoot.AddUdonSharpComponent<UtsushiSource>();
                UdonSharpEditorUtility.CopyProxyToUdon(source);
                SavePrefab(sourceRoot, SourcePrefab, "Utsushi Source");
            }
            finally { if (sourceRoot != null) Object.DestroyImmediate(sourceRoot); }

            BuildDisplayPrefab(font, ui, tmp, true, DisplayPrefab);
            BuildDisplayPrefab(font, ui, tmp, false, DisplayDarkPrefab);
            AssetDatabase.SaveAssets();
            Debug.Log("[Utsushi] Build Prefabs complete.");
        }

        private static void BuildDisplayPrefab(TMP_FontAsset font, Material ui, Material tmp, bool lightTheme, string path)
        {
            DestroyLeftoverBuildRoot(DisplayTemp);
            GameObject root = new GameObject(DisplayTemp);
            try
            {
            UtsushiDisplay display = root.AddUdonSharpComponent<UtsushiDisplay>();
            display.showGrid = true;
            display.gridWidth = 1f;
            display.textScale = 1.4f;
            display.zebra = false;
            Canvas canvas = CreateCanvas(root.transform, "Canvas", new Vector2(1600f, 1000f));
            Image background = CreateImage(canvas.transform, "Background", lightTheme ? Color.white : new Color32(0x12, 0x16, 0x1C, 0xFF));
            display.background = background;
            SetEdges(background.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            display.titleText = CreateText(canvas.transform, "Title", 52f, font, TextAlignmentOptions.MidlineLeft);
            display.titleText.color = lightTheme ? new Color32(0x20, 0x21, 0x24, 0xFF) : new Color32(0xE8, 0xED, 0xF2, 0xFF);
            SetEdges(display.titleText.rectTransform, new Vector2(0, 1), Vector2.one, new Vector2(40, -90), new Vector2(-40, 0));
            GameObject table = new GameObject("TableArea", typeof(RectTransform));
            table.transform.SetParent(canvas.transform, false);
            display.tableArea = table.GetComponent<RectTransform>();
            SetEdges(display.tableArea, Vector2.zero, Vector2.one, new Vector2(40, 80), new Vector2(-40, -110));
            GameObject frame = new GameObject("ColumnTemplate", typeof(RectTransform), typeof(RectMask2D));
            frame.transform.SetParent(table.transform, false);
            SetEdges(frame.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            display.columnTemplate = CreateText(frame.transform, "Text", 32f, font, TextAlignmentOptions.TopLeft);
            display.columnTemplate.overflowMode = TextOverflowModes.Overflow;
            display.columnTemplate.richText = true;
            display.columnTemplate.margin = Vector4.zero;
            SetEdges(display.columnTemplate.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            frame.SetActive(false);
            display.overlayTemplate = CreateOverlayTemplate(table.transform, font, "OverlayTemplate", true);
            display.overlayNoWrapTemplate = CreateOverlayTemplate(table.transform, font, "OverlayNoWrapTemplate", false);
            display.cellTemplate = CreateImage(table.transform, "CellTemplate", Color.white);
            SetEdges(display.cellTemplate.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            display.cellTemplate.gameObject.SetActive(false);
            GameObject chipLayer = new GameObject("ChipLayer", typeof(RectTransform));
            chipLayer.transform.SetParent(table.transform, false);
            SetEdges(chipLayer.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Sprite chipSprite = EnsureShapeSprite("Utsushi Chip.png", 64, 64, true);
            display.chipTemplate = CreateImage(chipLayer.transform, "ChipTemplate", Color.white);
            display.chipTemplate.sprite = chipSprite;
            display.chipTemplate.type = Image.Type.Sliced;
            display.chipTemplate.gameObject.SetActive(false);
            GameObject arrowLayer = new GameObject("ArrowLayer", typeof(RectTransform));
            arrowLayer.transform.SetParent(table.transform, false);
            SetEdges(arrowLayer.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Sprite arrowSprite = EnsureShapeSprite("Utsushi Arrow.png", 64, 40, false);
            display.arrowTemplate = CreateImage(arrowLayer.transform, "ArrowTemplate", Color.white);
            display.arrowTemplate.sprite = arrowSprite;
            display.arrowTemplate.type = Image.Type.Simple;
            display.arrowTemplate.gameObject.SetActive(false);
            display.statusDot = CreateImage(canvas.transform, "StatusDot", lightTheme ? new Color32(0xD9, 0x30, 0x25, 0xFF) : new Color32(0xF2, 0x8B, 0x82, 0xFF));
            SetEdges(display.statusDot.rectTransform, Vector2.zero, Vector2.zero, new Vector2(40, 28), new Vector2(60, 48));
            display.statusText = CreateText(canvas.transform, "Status", 28f, font, TextAlignmentOptions.MidlineLeft);
            display.statusText.color = display.titleText.color;
            SetEdges(display.statusText.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(75, 10), new Vector2(-250, 70));
            Button button = CreateButton(canvas.transform, font, lightTheme);
            display.refreshButton = button;
            display.refreshLabel = button.GetComponentInChildren<TMP_Text>();
            ApplyTheme(display, lightTheme);
            SetEdges(button.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-220, 15), new Vector2(-40, 65));
            UnityEventTools.AddStringPersistentListener(button.onClick, UdonSharpEditorUtility.GetBackingUdonBehaviour(display).SendCustomEvent, "OnRefreshPressed");
            ApplyMaterials(root, font, ui, tmp);
            UdonSharpEditorUtility.CopyProxyToUdon(display);
            SavePrefab(root, path, lightTheme ? "Utsushi Display" : "Utsushi Display Dark");
            }
            finally { if (root != null) Object.DestroyImmediate(root); }
        }

        internal static void ExportUnityPackage()
        {
            if (!CheckProgramsCompiled()) throw new System.InvalidOperationException("Utsushi programs are not compiled.");
            // Include only our generated programs; IncludeDependencies would also bundle the SDK.
            string[] paths = { RootDir.TrimEnd('/'), CompiledProgramPath(SourcePath), CompiledProgramPath(DisplayPath) };
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null) throw new System.InvalidOperationException("Missing Utsushi font: " + FontPath);
            font.ClearFontAssetData(true);
            EditorUtility.SetDirty(font);
            AssetDatabase.SaveAssets();
            AssetDatabase.ExportPackage(paths, "Utsushi.unitypackage", ExportPackageOptions.Recurse);
            Debug.Log("[Utsushi] Exported Utsushi.unitypackage");
        }

        private static string CompiledProgramPath(string sourcePath)
        {
            UdonSharpProgramAsset asset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(sourcePath + ".asset");
            string path = asset == null ? "" : AssetDatabase.GetAssetPath(asset.GetSerializedUdonProgramAsset());
            if (string.IsNullOrEmpty(path)) throw new System.InvalidOperationException("Missing compiled Utsushi program: " + sourcePath);
            return path;
        }

        [MenuItem("Tools/TenteEEEE/Utsushi/掲示板をシーンに置く")]
        public static void InstallSampleMenu()
        {
            if (Shader.Find("TextMeshPro/Mobile/Distance Field") == null)
            {
                if (EditorUtility.DisplayDialog("Utsushi", "TextMesh Pro Essential Resources がありません。インポートしますか？", "インポートする", "キャンセル"))
                {
                    TMPro.TMP_PackageResourceImporter.ImportResources(true, false, false);
                    EditorUtility.DisplayDialog("Utsushi", "インポートが終わったら、もう一度「掲示板をシーンに置く」を実行してください。", "OK");
                }
                return;
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefab) == null || AssetDatabase.LoadAssetAtPath<GameObject>(DisplayPrefab) == null)
            {
                EditorUtility.DisplayDialog("Utsushi", "必要な prefab が見つかりません。パッケージを再インポートしてください。", "OK");
                return;
            }
            if (!CheckProgramsCompiled()) return;
            Scene scene = SceneManager.GetActiveScene();
            UtsushiSource[] sources = Object.FindObjectsByType<UtsushiSource>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            UtsushiSource source = null;
            int sourceCount = 0;
            foreach (UtsushiSource candidate in sources)
                if (candidate.gameObject.scene == scene && candidate.gameObject.activeInHierarchy && candidate.enabled)
                { if (source == null) source = candidate; sourceCount++; }
            if (sourceCount > 1) Debug.Log("データ元が複数あるため『" + source.gameObject.name + "』につなぎました", source);
            if (source == null) source = InstantiatePrefab(SourcePrefab, scene).GetComponent<UtsushiSource>();
            GameObject board = InstantiatePrefab(DisplayPrefab, scene);
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                board.transform.position = sceneView.pivot;
                board.transform.rotation = Quaternion.Euler(0f, sceneView.camera.transform.eulerAngles.y, 0f);
                Canvas canvas = board.GetComponentInChildren<Canvas>(true);
                float width = canvas == null ? 1.6f : ((RectTransform)canvas.transform).rect.width * Mathf.Abs(canvas.transform.lossyScale.x);
                UtsushiDisplay[] existingBoards = Object.FindObjectsByType<UtsushiDisplay>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    bool occupied = false;
                    foreach (UtsushiDisplay existing in existingBoards)
                        if (existing != null && existing.gameObject != board && existing.gameObject.scene == scene &&
                            Vector3.Distance(existing.transform.position, board.transform.position) < width)
                        { occupied = true; break; }
                    if (!occupied) break;
                    board.transform.position += Vector3.right * width;
                }
            }
            else
            {
                float x = 0f;
                foreach (UtsushiDisplay existing in Object.FindObjectsByType<UtsushiDisplay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (existing != null && existing.gameObject != board && existing.gameObject.scene == scene)
                        x = Mathf.Max(x, existing.transform.position.x + 2f);
                board.transform.position = new Vector3(x, 1.5f, 0);
            }
            UtsushiDisplay display = board.GetComponent<UtsushiDisplay>();
            RecordProxyUndo(display, "Configure Utsushi Display");
            display.source = source;
            UdonSharpEditorUtility.CopyProxyToUdon(display);
            EditorUtility.SetDirty(display);
            PrefabUtility.RecordPrefabInstancePropertyModifications(display);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = board;
            Debug.Log("[Utsushi] 掲示板をシーンに置きました。", board);
        }

        [MenuItem("Tools/TenteEEEE/Utsushi/掲示板をシーンに置く", true)]
        private static bool ValidateInstallSample() { return !EditorApplication.isPlayingOrWillChangePlaymode; }

        private static GameObject InstantiatePrefab(string path, Scene scene)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new System.InvalidOperationException("Missing prefab: " + path);
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            Undo.RegisterCreatedObjectUndo(instance, "Install Utsushi");
            return instance;
        }

        private static bool AllProgramsCompiled()
        {
            foreach (System.Type type in new[] { typeof(UtsushiSource), typeof(UtsushiDisplay) })
            {
                UdonSharpProgramAsset asset = UdonSharpProgramAsset.GetProgramAssetForClass(type);
                if (asset == null || asset.GetRealProgram() == null) return false;
            }
            return true;
        }
        internal static bool CheckProgramsCompiled()
        {
            if (!UdonSharpProgramAsset.AnyUdonSharpScriptHasError() && AllProgramsCompiled()) return true;
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            if (!UdonSharpProgramAsset.AnyUdonSharpScriptHasError() && AllProgramsCompiled()) return true;
            EditorUtility.DisplayDialog("Utsushi", ProgramFailureMessage(), "OK");
            return false;
        }
        internal static bool ProgramsCompiledQuiet() => !UdonSharpProgramAsset.AnyUdonSharpScriptHasError() && AllProgramsCompiled();
        internal static string ProgramFailureMessage() => UdonSharpProgramAsset.AnyUdonSharpScriptHasError()
            ? "UdonSharp のコンパイルエラーがあります（Utsushi 以外のスクリプトのエラーでも止まります）。Console のエラーを直してから、もう一度実行してください。"
            : "Utsushi のプログラムが見つかりません。Unity を再起動するか、パッケージを入れ直してください。";

        internal static void ApplyTheme(UtsushiDisplay display, bool lightTheme)
        {
            if (display == null) return;
            display.lightTheme = lightTheme;
            display.chipPalette = lightTheme
                ? new Color[] { new Color32(0xF8, 0xD7, 0xDA, 0xFF), new Color32(0xFD, 0xE2, 0xC0, 0xFF), new Color32(0xFF, 0xF1, 0xB0, 0xFF), new Color32(0xD6, 0xF0, 0xCF, 0xFF), new Color32(0xCB, 0xED, 0xE7, 0xFF), new Color32(0xD2, 0xE4, 0xFB, 0xFF), new Color32(0xE1, 0xDA, 0xF6, 0xFF), new Color32(0xF6, 0xD6, 0xEA, 0xFF), new Color32(0xE8, 0xE1, 0xD2, 0xFF), new Color32(0xD8, 0xE0, 0xE7, 0xFF) }
                : new Color[] { new Color32(0x7A, 0x2E, 0x3A, 0xFF), new Color32(0x7A, 0x4A, 0x16, 0xFF), new Color32(0x66, 0x57, 0x0F, 0xFF), new Color32(0x2F, 0x5E, 0x32, 0xFF), new Color32(0x1F, 0x5C, 0x56, 0xFF), new Color32(0x24, 0x49, 0x7A, 0xFF), new Color32(0x4A, 0x3B, 0x7A, 0xFF), new Color32(0x6E, 0x2E, 0x5C, 0xFF), new Color32(0x5A, 0x4E, 0x3A, 0xFF), new Color32(0x3C, 0x4A, 0x57, 0xFF) };
            display.chipColor = lightTheme ? new Color32(0xE8, 0xEA, 0xED, 0xFF) : new Color32(0x3C, 0x40, 0x43, 0xFF);
            display.chipArrowColor = lightTheme ? new Color32(0x5F, 0x63, 0x68, 0xFF) : new Color32(0x9A, 0xA0, 0xA6, 0xFF);
            if (display.background != null) display.background.color = lightTheme ? Color.white : new Color32(0x12, 0x16, 0x1C, 0xFF);
            Color text = lightTheme ? new Color32(0x20, 0x21, 0x24, 0xFF) : new Color32(0xE8, 0xED, 0xF2, 0xFF);
            if (display.titleText != null) display.titleText.color = text;
            if (display.statusText != null) display.statusText.color = text;
            if (display.statusDot != null) display.statusDot.color = lightTheme ? new Color32(0xD9, 0x30, 0x25, 0xFF) : new Color32(0xF2, 0x8B, 0x82, 0xFF);
            if (display.refreshButton != null)
            {
                Image image = display.refreshButton.GetComponent<Image>();
                if (image != null) image.color = lightTheme ? new Color32(0x0F, 0x76, 0x6E, 0xFF) : new Color32(0x1F, 0x6F, 0x69, 0xFF);
            }
            if (display.refreshLabel != null) display.refreshLabel.color = lightTheme ? new Color32(0xFF, 0xFF, 0xFF, 0xFF) : new Color32(0xF1, 0xF5, 0xF7, 0xFF);
        }
        private static void EnsureProgramAssets()
        {
            bool created = EnsureProgramAsset(SourcePath) | EnsureProgramAsset(DisplayPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();
            if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
                throw new System.InvalidOperationException("UdonSharp のコンパイルエラーがあります（Utsushi 以外のスクリプトのエラーでも止まります）。Console のエラーを直してから、もう一度実行してください。");
            if (UdonSharpProgramAsset.GetProgramAssetForClass(typeof(UtsushiSource)) == null || UdonSharpProgramAsset.GetProgramAssetForClass(typeof(UtsushiDisplay)) == null)
                throw new System.InvalidOperationException("[Utsushi] Failed to associate UdonSharp program assets.");
            if (created) throw new System.InvalidOperationException("[Utsushi] UdonSharp のプログラムを作成しました。Build Prefabs をもう一度実行してください（初回だけ必要です）。");
        }
        private static bool EnsureProgramAsset(string path)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path + ".cs");
            if (script == null) throw new System.InvalidOperationException("Missing UdonSharp source: " + path);
            UdonSharpProgramAsset asset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(path + ".asset");
            if (asset == null) { asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>(); asset.sourceCsScript = script; AssetDatabase.CreateAsset(asset, path + ".asset"); return true; }
            if (asset.sourceCsScript != script) { asset.sourceCsScript = script; EditorUtility.SetDirty(asset); }
            return false;
        }
        private static TMP_FontAsset EnsureFontAsset()
        {
            Font source = AssetDatabase.LoadAssetAtPath<Font>(RootDir + "Fonts/NotoSansJP-Regular.otf");
            if (source == null) throw new System.InvalidOperationException("Missing Noto Sans JP source font.");
            TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
            if (font == null)
            {
                if (Shader.Find("TextMeshPro/Mobile/Distance Field") == null) throw new System.InvalidOperationException("Import TMP Essential Resources first.");
                font = TMP_FontAsset.CreateFontAsset(source, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
                font.name = "Utsushi JP Dynamic"; AssetDatabase.CreateAsset(font, FontPath);
            }
            if (font.sourceFontFile == null) throw new System.InvalidOperationException("Dynamic font has no sourceFontFile: " + FontPath);
            font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
            font.ClearFontAssetData(true);
            SerializedObject serialized = new SerializedObject(font);
            serialized.FindProperty("m_ClearDynamicDataOnBuild").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Texture2D[] atlases = font.atlasTextures;
            if (atlases != null) for (int i = 0; i < atlases.Length; i++) if (atlases[i] != null && !AssetDatabase.IsSubAsset(atlases[i])) { atlases[i].name = "Utsushi JP Atlas " + i; AssetDatabase.AddObjectToAsset(atlases[i], font); }
            if (font.material != null && !AssetDatabase.IsSubAsset(font.material)) { font.material.name = "Utsushi JP Material"; AssetDatabase.AddObjectToAsset(font.material, font); }
            EditorUtility.SetDirty(font); AssetDatabase.SaveAssets(); return font;
        }
        private static void EnsureDirectories()
        {
            if (!AssetDatabase.IsValidFolder(RootDir.TrimEnd('/'))) throw new System.InvalidOperationException("Missing root: " + RootDir);
            foreach (string folder in new[] { "Prefabs", "Generated" }) if (!AssetDatabase.IsValidFolder(RootDir + folder)) AssetDatabase.CreateFolder(RootDir.TrimEnd('/'), folder);
        }
        private static Material EnsureUiMaterial()
        {
            string path = RootDir + "Generated/Utsushi UI Material.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { Shader shader = Shader.Find("UI/Default"); if (shader == null) throw new System.InvalidOperationException("Missing UI/Default shader."); material = new Material(shader) { name = "Utsushi UI Material" }; AssetDatabase.CreateAsset(material, path); }
            material.renderQueue = 3000; material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual); EditorUtility.SetDirty(material); return material;
        }
        private static Sprite EnsureShapeSprite(string fileName, int width, int height, bool circle)
        {
            string path = RootDir + "Generated/" + fileName;
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            Color32[] pixels = new Color32[width * height];
            const int samples = 4;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int covered = 0;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float px = x + (sx + 0.5f) / samples;
                            float py = y + (sy + 0.5f) / samples;
                            if (circle)
                            {
                                float dx = px - width * 0.5f;
                                float dy = py - height * 0.5f;
                                if (dx * dx + dy * dy <= width * width * 0.25f) covered++;
                            }
                            else if (px >= width * 0.5f - py * (width / (float)height) * 0.5f && px <= width * 0.5f + py * (width / (float)height) * 0.5f) covered++;
                        }
                    }
                    byte alpha = (byte)(covered * 255 / (samples * samples));
                    pixels[y * width + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new System.InvalidOperationException("Missing texture importer: " + path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.filterMode = FilterMode.Bilinear;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.spriteBorder = circle ? new Vector4(31f, 31f, 31f, 31f) : Vector4.zero;
            importer.SaveAndReimport();
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new System.InvalidOperationException("Unable to load generated sprite: " + path);
            return sprite;
        }

        private static Material EnsureTmpMaterial(TMP_FontAsset font)
        {
            string path = RootDir + "Generated/Utsushi TMP Material.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { if (font.material == null) throw new System.InvalidOperationException("Missing font material."); material = new Material(font.material) { name = "Utsushi TMP Material" }; AssetDatabase.CreateAsset(material, path); }
            material.renderQueue = 3000; material.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual); material.SetFloat("_WeightBold", BoldWeight); EditorUtility.SetDirty(material); return material;
        }
        private static TMP_Text CreateOverlayTemplate(Transform table, TMP_FontAsset font, string name, bool wrap)
        {
            GameObject frame = new GameObject(name, typeof(RectTransform), typeof(RectMask2D));
            frame.transform.SetParent(table, false);
            SetEdges(frame.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            TMP_Text text = CreateText(frame.transform, "Text", 32f, font, TextAlignmentOptions.Center);
            text.enableWordWrapping = wrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = true;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            SetEdges(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            frame.SetActive(false);
            return text;
        }
        internal static void RecordProxyUndo(UdonSharpBehaviour proxy, string name)
        {
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            if (backing == null) Undo.RecordObject(proxy, name);
            else Undo.RecordObjects(new Object[] { proxy, backing }, name);
        }
        private static void ApplyMaterials(GameObject root, TMP_FontAsset font, Material ui, Material tmp)
        {
            foreach (Image image in root.GetComponentsInChildren<Image>(true)) image.material = ui;
            foreach (TextMeshProUGUI text in root.GetComponentsInChildren<TextMeshProUGUI>(true)) { text.font = font; text.fontSharedMaterial = tmp; }
        }
        private static void SavePrefab(GameObject root, string path, string name) { root.name = name; PrefabUtility.SaveAsPrefabAsset(root, path); }
        private static void DestroyLeftoverBuildRoot(string name)
        {
            Scene scene = SceneManager.GetActiveScene(); if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (GameObject root in scene.GetRootGameObjects()) if (root.name == name) Object.DestroyImmediate(root);
        }
    }
}
#endif
