using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // LOUDNESS PRESCAN PROGRESS BAR
    // ─────────────────────────────────────────────────────────────────────────
    // A bottom-centre overlay shown only while a background loudness scan is actively
    // measuring the library. Driven by the _prescan* counters in
    // ContextMusicManager.AudioPlayer.cs and drawn from the unified OnGUI() in
    // ContextMusicManager.Keybinds.cs (a MonoBehaviour may declare OnGUI() only once).
    // Auto-hides the instant the scan finishes.
    //
    // Currently gated to the full-library proactive scan (CMMConfig.LoudnessProactiveScan,
    // off by default): the bounded per-context menu prewarm also drives the _prescan*
    // counters, but it's quick and we don't want a progress bar flashing on the intro
    // screen for it. The drawing code is kept intact so re-enabling a full-scan feature
    // brings the HUD back automatically.

    public partial class ContextMusicManager
    {
        private Texture2D _preBg, _preBarBg, _preBarFill;
        private GUIStyle _preTitleStyle, _preCountStyle;

        private void DrawLoudnessPrescan()
        {
            // Disabled while the proactive full scan is off (the default). Keeps the HUD
            // out of the intro screen for the quick bounded menu prewarm.
            if (!CMMConfig.LoudnessProactiveScan) return;
            if (!_prescanActive || _prescanTotal <= 0) return;

            EnsurePrescanStyles();

            int done = _prescanDone;
            int total = _prescanTotal;
            float frac = Mathf.Clamp01(total > 0 ? (float)done / total : 0f);

            const float w = 460f, h = 74f;
            float x = (Screen.width - w) * 0.5f;
            float y = Screen.height - h - 90f;

            GUI.DrawTexture(new Rect(x, y, w, h), _preBg, ScaleMode.StretchToFill);

            GUI.Label(new Rect(x + 16f, y + 8f, w - 32f, 22f),
                      "Analyzing music loudness…", _preTitleStyle);
            GUI.Label(new Rect(x + 16f, y + 8f, w - 32f, 22f),
                      $"{done} / {total}", _preCountStyle);

            float barX = x + 16f, barY = y + 40f, barW = w - 32f, barH = 16f;
            GUI.DrawTexture(new Rect(barX, barY, barW, barH), _preBarBg, ScaleMode.StretchToFill);
            GUI.DrawTexture(new Rect(barX, barY, barW * frac, barH), _preBarFill, ScaleMode.StretchToFill);
        }

        private void EnsurePrescanStyles()
        {
            if (_preBg == null)
            {
                _preBg      = MakeTex(new Color(0f, 0f, 0f, 0.80f));
                _preBarBg   = MakeTex(new Color(1f, 1f, 1f, 0.16f));
                _preBarFill = MakeTex(new Color(1f, 0.78f, 0.35f, 1f));   // amber, matches the volume popup
            }
            if (_preTitleStyle == null)
            {
                _preTitleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleLeft,
                };
                _preTitleStyle.normal.textColor = Color.white;
            }
            if (_preCountStyle == null)
            {
                _preCountStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleRight,
                };
                _preCountStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
        }
    }
}
