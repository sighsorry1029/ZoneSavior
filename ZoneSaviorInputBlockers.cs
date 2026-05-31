using System;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZoneSavior;

internal static class ZoneSaviorInputBlockers
{
    private static ManualLogSource? _logger;
    private static bool _useTextInputFallback;
    private static bool _loggedTextInputFallback;

    internal static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
    }

    internal static bool IsTextInputVisible()
    {
        if (!_useTextInputFallback)
        {
            try
            {
                return TextInput.IsVisible() || IsFocusedInputField();
            }
            catch (Exception ex)
            {
                _useTextInputFallback = true;
                LogTextInputFallback(ex);
            }
        }

        return IsValheimTextInputVisible() || IsFocusedInputField();
    }

    private static bool IsValheimTextInputVisible()
    {
        TextInput textInput = TextInput.instance;
        return textInput && textInput.m_visibleFrame;
    }

    private static bool IsFocusedInputField()
    {
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem == null)
        {
            return false;
        }

        GameObject selected = eventSystem.currentSelectedGameObject;
        if (!selected)
        {
            return false;
        }

        InputField inputField = selected.GetComponent<InputField>() ?? selected.GetComponentInParent<InputField>();
        if (inputField != null && inputField.isFocused)
        {
            return true;
        }

        TMP_InputField tmpInputField = selected.GetComponent<TMP_InputField>() ?? selected.GetComponentInParent<TMP_InputField>();
        return tmpInputField != null && tmpInputField.isFocused;
    }

    private static void LogTextInputFallback(Exception ex)
    {
        if (_loggedTextInputFallback)
        {
            return;
        }

        _loggedTextInputFallback = true;
        _logger?.LogWarning(
            $"TextInput.IsVisible failed ({ex.GetType().Name}: {ex.Message}); using ZoneSavior's safe input visibility fallback.");
    }
}
