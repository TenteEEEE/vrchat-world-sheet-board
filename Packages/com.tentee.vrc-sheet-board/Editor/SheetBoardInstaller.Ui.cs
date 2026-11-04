#if UNITY_EDITOR
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;

namespace SheetBoardEditor
{
    public static partial class SheetBoardInstaller
    {
        private static Canvas CreateCanvas(Transform parent, string name, Vector2 size)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            obj.transform.SetParent(parent, false);
            obj.layer = 0;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one * 0.001f;
            obj.GetComponent<RectTransform>().sizeDelta = size;
            Canvas canvas = obj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 0;
            obj.AddComponent<VRCUiShape>();
            BoxCollider collider = obj.AddComponent<BoxCollider>();
            collider.size = new Vector3(size.x, size.y, 6f);
            collider.center = Vector3.zero;
            collider.isTrigger = true;
            return canvas;
        }
        private static Image CreateImage(Transform parent, string name, Color color)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            obj.transform.SetParent(parent, false);
            Image image = obj.GetComponent<Image>();
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }
        private static TextMeshProUGUI CreateText(Transform parent, string name, float size, TMP_FontAsset font, TextAlignmentOptions alignment)
        {
            GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            obj.transform.SetParent(parent, false);
            TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.color = new Color32(0xE8, 0xED, 0xF2, 0xFF);
            text.alignment = alignment;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            return text;
        }
        private static Button CreateButton(Transform parent, TMP_FontAsset font, bool lightTheme)
        {
            GameObject obj = new GameObject("RefreshButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            obj.transform.SetParent(parent, false);
            Image image = obj.GetComponent<Image>();
            image.color = lightTheme ? new Color32(0x0F, 0x76, 0x6E, 0xFF) : new Color32(0x1F, 0x6F, 0x69, 0xFF);
            image.raycastTarget = true;
            Button button = obj.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
            colors.pressedColor = new Color(0.80f, 0.80f, 0.80f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = Color.white;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.1f;
            button.colors = colors;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            TextMeshProUGUI label = CreateText(obj.transform, "Label", 28f, font, TextAlignmentOptions.Center);
            label.color = lightTheme ? new Color32(0xFF, 0xFF, 0xFF, 0xFF) : new Color32(0xF1, 0xF5, 0xF7, 0xFF);
            label.text = "更新";
            SetEdges(label.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return button;
        }
        private static void SetEdges(RectTransform rect, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
        }
    }
}
#endif
