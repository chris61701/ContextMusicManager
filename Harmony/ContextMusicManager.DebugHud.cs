// Compile-time gated: included only when CMM_DEBUG_HUD is defined (Debug builds, or
// any build with -p:EnableDebugHud=true). Release/shipping builds strip it entirely.
#if CMM_DEBUG_HUD
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // DEBUG HUD
    // ─────────────────────────────────────────────────────────────────────────
    // A small top-left overlay showing the active decision source (the on-disk
    // context.lua or the embedded default), the context currently playing, and the
    // tokens the script last resolved. Toggled with Numpad 8; hidden by default and
    // purely diagnostic — it reads ScriptHost state and never influences playback.

    public partial class ContextMusicManager
    {
        private static bool _threatHudVisible = false;

        private GUIStyle _hudBodyStyle;
        private GUIStyle _hudHeaderStyle;
        private Texture2D _hudBgTex;

        private void Update()
        {
            // Numpad 8 toggles the HUD. Cheap per-frame check, no allocation.
            if (Input.GetKeyDown(KeyCode.Keypad8))
            {
                _threatHudVisible = !_threatHudVisible;
                CMMConfig.D($"[CMM] Context HUD {(_threatHudVisible ? "shown" : "hidden")}.");
            }
        }

        private void OnGUI()
        {
            if (!_threatHudVisible || !inGame || playerIsDead) return;

            EnsureHudStyles();

            string source = ScriptHost.Active
                ? (ScriptHost.UsingEmbeddedDefault ? "context.lua (embedded default)" : "context.lua")
                : "NONE — script failed to load";
            var resolved = ScriptHost.LastContexts;
            string active = (resolved != null && resolved.Count > 0) ? string.Join(", ", resolved) : "(none)";
            string body =
                $"Source:   {source}\n" +
                $"Playing:  {currentContext?.Context ?? "null"}\n" +
                $"Resolved: {active}";

            const float x = 10f, y = 10f, w = 420f, h = 150f;
            var box = new Rect(x, y, w, h);

            // Dark translucent backdrop so the text stays legible over any scene.
            GUI.DrawTexture(box, _hudBgTex, ScaleMode.StretchToFill);
            GUI.Label(new Rect(x + 12f, y + 8f, w - 24f, 22f), "CMM • Context  [Num8]", _hudHeaderStyle);
            GUI.Label(new Rect(x + 12f, y + 34f, w - 24f, h - 40f), body, _hudBodyStyle);
        }

        private void EnsureHudStyles()
        {
            if (_hudBgTex == null)
            {
                _hudBgTex = new Texture2D(1, 1);
                _hudBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
                _hudBgTex.Apply();
            }
            if (_hudBodyStyle == null)
            {
                _hudBodyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = true
                };
                _hudBodyStyle.normal.textColor = Color.white;
            }
            if (_hudHeaderStyle == null)
            {
                _hudHeaderStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft
                };
                _hudHeaderStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
        }
    }
}
#endif
