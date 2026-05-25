using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private static Sprite GetIcon(bool slope, bool reset, bool paint, bool paintReset)
    {
        if (paintReset && _paintResetIcon)
        {
            return _paintResetIcon;
        }

        if (paint && _paintIcon)
        {
            return _paintIcon;
        }

        if (reset && _resetIcon)
        {
            return _resetIcon;
        }

        if (!paint && !paintReset && !reset && !slope && _icon)
        {
            return _icon;
        }

        if (!paint && !paintReset && !reset && slope && _slopeIcon)
        {
            return _slopeIcon;
        }

        Texture2D texture = new(64, 64, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        Color clear = new(0f, 0f, 0f, 0f);
        Color background = new(0.08f, 0.09f, 0.12f, 0.95f);
        Color ring = new(1f, 0.05f, 0.85f, 1f);
        Color ringGlow = new(1f, 0.05f, 0.85f, 0.32f);
        Color paintColor = new(0.95f, 0.72f, 0.18f, 1f);
        Color paintDark = new(0.38f, 0.2f, 0.04f, 1f);
        Color resetColor = new(1f, 0.22f, 0.18f, 1f);
        Color resetDark = new(0.36f, 0.06f, 0.04f, 1f);
        Color terrain = paint
            ? paintColor
            : paintReset
            ? paintColor
            : reset
            ? resetColor
            : slope
                ? new Color(0.35f, 0.78f, 0.86f, 1f)
                : new Color(0.18f, 0.72f, 0.48f, 1f);
        Color terrainDark = paint
            ? paintDark
            : paintReset
            ? paintDark
            : reset
            ? resetDark
            : slope
                ? new Color(0.07f, 0.24f, 0.32f, 1f)
                : new Color(0.05f, 0.24f, 0.16f, 1f);
        Vector2 center = new(31.5f, 31.5f);

        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center);
                Color pixel = clear;
                if (dist <= 28f)
                {
                    pixel = background;
                }

                if (dist >= 21f && dist <= 25f)
                {
                    pixel = ring;
                }
                else if (dist >= 17.5f && dist < 21f)
                {
                    pixel = Color.Lerp(pixel, ringGlow, 0.65f);
                }

                bool mound = y >= 31 && y <= 47 && x >= 16 && x <= 48;
                if (!slope && !reset && !paint && !paintReset && mound)
                {
                    float dx = Mathf.Abs(x - 32f) / 16f;
                    float dy = (y - 31f) / 16f;
                    if (dy >= dx * 0.55f)
                    {
                        pixel = dy > 0.75f ? terrainDark : terrain;
                    }
                }

                if (paint || paintReset)
                {
                    bool brushHead = Vector2.Distance(new Vector2(x, y), new Vector2(30f, 28f)) <= 9f;
                    bool brushHandle = x >= 37 && x <= 49 && y >= 39 && y <= 45 && Mathf.Abs((y - 42f) - (x - 43f) * 0.16f) <= 3f;
                    bool stroke = y >= 44 && y <= 48 && x >= 17 && x <= 43 && Mathf.Sin((x - 17f) * 0.5f) > -0.35f;
                    if (brushHead || stroke)
                    {
                        pixel = terrain;
                    }
                    else if (brushHandle)
                    {
                        pixel = terrainDark;
                    }
                }

                if (paintReset &&
                    ((Mathf.Abs(x - y) <= 2 && x >= 18 && x <= 46) ||
                     (Mathf.Abs((x + y) - 64) <= 2 && x >= 18 && x <= 46)))
                {
                    pixel = ring;
                }

                if (reset)
                {
                    bool eraseHead = Vector2.Distance(new Vector2(x, y), new Vector2(30f, 30f)) <= 11f;
                    bool eraseHandle = x >= 38 && x <= 49 && y >= 40 && y <= 45 && Mathf.Abs((y - 42.5f) - (x - 43.5f) * 0.18f) <= 3f;
                    if (eraseHead)
                    {
                        pixel = terrain;
                    }
                    else if (eraseHandle)
                    {
                        pixel = terrainDark;
                    }

                    if ((Mathf.Abs(x - y) <= 2 && x >= 18 && x <= 46) ||
                        (Mathf.Abs((x + y) - 64) <= 2 && x >= 18 && x <= 46))
                    {
                        pixel = ring;
                    }
                }

                if (slope)
                {
                    float lineY = 47f - (x - 14f) * 0.62f;
                    if (x >= 14 && x <= 50 && Mathf.Abs(y - lineY) <= 2f)
                    {
                        pixel = terrain;
                    }
                    else if (x >= 14 && x <= 50 && y > lineY && y < 49)
                    {
                        pixel = Color.Lerp(terrainDark, terrain, Mathf.Clamp01((49f - y) / 18f));
                    }

                    if ((Mathf.Abs(x - 16) <= 1 && y >= 41 && y <= 50) ||
                        (Mathf.Abs(x - 50) <= 1 && y >= 18 && y <= 29))
                    {
                        pixel = ring;
                    }
                }

                if (!slope && !reset && !paint && !paintReset &&
                    ((Mathf.Abs(x - 32) <= 1 && y >= 12 && y <= 21) ||
                     (Mathf.Abs(y - 32) <= 1 && x >= 12 && x <= 21) ||
                     (Mathf.Abs(y - 32) <= 1 && x >= 43 && x <= 52)))
                {
                    pixel = ring;
                }

                texture.SetPixel(x, y, pixel);
            }
        }

        texture.Apply();
        Sprite icon = Sprite.Create(texture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 64f);
        if (paintReset)
        {
            _paintResetIcon = icon;
        }
        else if (paint)
        {
            _paintIcon = icon;
        }
        else if (reset)
        {
            _resetIcon = icon;
        }
        else if (slope)
        {
            _slopeIcon = icon;
        }
        else
        {
            _icon = icon;
        }

        return icon;
    }
}
