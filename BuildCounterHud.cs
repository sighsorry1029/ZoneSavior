using TMPro;
using UnityEngine;

namespace ZoneSavior;

internal sealed class BuildCounterHud : MonoBehaviour
{
    private const float FadeDuration = 0.15f;

    private static BuildCounterHud? _instance;

    private CanvasGroup? _canvasGroup;
    private TextMeshProUGUI? _text;
    private float _hideAt = float.MinValue;

    public static void ShowCount(int count, int limit)
    {
        if (Hud.instance == null)
        {
            return;
        }

        EnsureInstance();
        _instance?.Show($"{count}/{limit}");
    }

    private static void EnsureInstance()
    {
        if (Hud.instance == null)
        {
            return;
        }

        if (_instance != null && _instance)
        {
            _instance.EnsureElements();
            return;
        }

        _instance = Hud.instance.GetComponent<BuildCounterHud>();
        if (_instance == null)
        {
            _instance = Hud.instance.gameObject.AddComponent<BuildCounterHud>();
        }

        _instance.EnsureElements();
    }

    private void Update()
    {
        if (_canvasGroup == null)
        {
            return;
        }

        if (Time.unscaledTime <= _hideAt)
        {
            _canvasGroup.alpha = 1f;
            return;
        }

        _canvasGroup.alpha = Mathf.MoveTowards(_canvasGroup.alpha, 0f, Time.unscaledDeltaTime / FadeDuration);
        if (_canvasGroup.alpha <= 0f && _text != null)
        {
            _text.text = string.Empty;
        }
    }

    private void Show(string value)
    {
        if (_text == null || _canvasGroup == null)
        {
            return;
        }

        _text.text = value;
        _canvasGroup.alpha = 1f;
        _hideAt = Time.unscaledTime + ClientConfig.CounterVisibleSeconds;
    }

    private void EnsureElements()
    {
        if (_text != null && _canvasGroup != null)
        {
            return;
        }

        TMP_Text? template = Hud.instance?.m_buildSelection;
        if (template == null)
        {
            return;
        }

        GameObject root = new("ZoneSaviorCounter", typeof(RectTransform), typeof(CanvasRenderer), typeof(CanvasGroup), typeof(TextMeshProUGUI));
        root.transform.SetParent(Hud.instance!.transform, false);

        RectTransform rectTransform = (RectTransform)root.transform;
        rectTransform.anchorMin = new Vector2(0.5f, 1f);
        rectTransform.anchorMax = new Vector2(0.5f, 1f);
        rectTransform.pivot = new Vector2(0.5f, 1f);
        rectTransform.anchoredPosition = new Vector2(0f, -18f);
        rectTransform.sizeDelta = new Vector2(280f, 34f);

        _canvasGroup = root.GetComponent<CanvasGroup>();
        _canvasGroup.alpha = 0f;

        _text = root.GetComponent<TextMeshProUGUI>();
        _text.font = template.font;
        _text.fontSharedMaterial = template.fontSharedMaterial;
        _text.fontSize = template.fontSize;
        _text.color = template.color;
        _text.alignment = TextAlignmentOptions.Center;
        _text.textWrappingMode = TextWrappingModes.NoWrap;
        _text.overflowMode = TextOverflowModes.Overflow;
        _text.raycastTarget = false;
        _text.text = string.Empty;
    }
}

