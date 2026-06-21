using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // VANILLA SECTION CLIPS
    //
    // When a context with a VanillaIfDMS fallback has no custom files and Dynamic
    // Music is on, instead of handing control to the game's Conductor (which is a
    // frozen, one-shot, unreliable path for traders), CMM borrows the game's own
    // DMS section AudioClip and plays it on its own channel — looped, faded, and
    // volume-controlled like any disk track. The Conductor stays blocked.
    //
    // Scope: only SingleClip sections are supported (traders + home). Multi-clip
    // ClipSet sections (Exploration/Suspense/Combat/Bloodmoon) are not handled and
    // keep the old "Vanilla" handoff.
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        // CMM context -> vanilla DMS SectionType name (matched against the enum's
        // ToString()). Only the five named traders are mapped; anything else with a
        // VanillaIfDMS fallback keeps the Conductor handoff.
        private static readonly Dictionary<string, string> _vanillaSectionMap =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Trader_Rekt", "TraderRekt" },
            { "Trader_Jen",  "TraderJen"  },
            { "Trader_Bob",  "TraderBob"  },
            { "Trader_Hugh", "TraderHugh" },
            { "Trader_Joel", "TraderJoel" },
        };

        // Reflection handles into DynamicMusic.Content / SingleClip — resolved once.
        // Mirrors how MixerController is reached, so this stays robust regardless of
        // the runtime assembly's member visibility.
        private static bool _vanillaReflectionResolved;
        private static FieldInfo    _allContentField;       // static List<Content> Content.AllContent
        private static PropertyInfo _contentSectionProp;    // SectionType Content.Section
        private static PropertyInfo _contentIsLoadedProp;   // bool      Content.IsLoaded
        private static PropertyInfo _singleClipClipProp;    // AudioClip SingleClip.Clip
        private static MethodInfo   _singleClipLoadMethod;  // IEnumerator SingleClip.Load()

        // Per-section caches, valid for one world session. Cleared on world exit
        // (CleanupAllCaches) since the DMS rebuilds its content on reload.
        private static readonly Dictionary<string, AudioClip> _vanillaClipReady =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _vanillaLoadKicked =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Drop the per-session vanilla-clip state. Called from CleanupAllCaches on
        // world exit so we never hand back an AudioClip from a previous world.
        public static void ClearVanillaClipState()
        {
            _vanillaClipReady.Clear();
            _vanillaLoadKicked.Clear();
        }

        private static void ResolveVanillaReflection()
        {
            _vanillaReflectionResolved = true;
            try
            {
                Assembly asm = typeof(DynamicMusic.Conductor).Assembly;
                Type contentType = asm.GetType("DynamicMusic.Content");
                Type singleClipType = asm.GetType("DynamicMusic.SingleClip");
                if (contentType == null || singleClipType == null)
                {
                    Log.Warning("[CMM] Vanilla clip reflection: Content/SingleClip type not found.");
                    return;
                }
                _allContentField = contentType.GetField("AllContent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _contentSectionProp = contentType.GetProperty("Section",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _contentIsLoadedProp = contentType.GetProperty("IsLoaded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _singleClipClipProp = singleClipType.GetProperty("Clip",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _singleClipLoadMethod = singleClipType.GetMethod("Load",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_allContentField == null || _contentSectionProp == null || _singleClipClipProp == null)
                    Log.Warning("[CMM] Vanilla clip reflection: one or more members not found.");
                else
                    CMMConfig.D("[CMM] Vanilla clip reflection resolved.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] Vanilla clip reflection failed: {ex.Message}");
            }
        }

        // Maps a CMM context to its vanilla DMS section name, or null if unsupported.
        public static string GetVanillaSectionName(string contextName) =>
            contextName != null && _vanillaSectionMap.TryGetValue(contextName, out string s) ? s : null;

        // True only when the context maps to a supported vanilla SingleClip section
        // whose AudioClip is loaded and ready to play right now. When the clip exists
        // but isn't loaded yet, kicks a one-time async Load() and returns false — the
        // caller falls back to the old "Vanilla" handoff until a later resolve tick.
        public static bool TryGetReadyVanillaClip(string contextName, out AudioClip clip)
        {
            clip = null;
            string section = GetVanillaSectionName(contextName);
            if (section == null) return false;

            if (_vanillaClipReady.TryGetValue(section, out AudioClip cached) && cached != null)
            {
                clip = cached;
                return true;
            }

            if (!_vanillaReflectionResolved) ResolveVanillaReflection();
            if (_allContentField == null || _contentSectionProp == null || _singleClipClipProp == null)
                return false;

            try
            {
                if (!(_allContentField.GetValue(null) is IEnumerable all)) return false;
                foreach (object item in all)
                {
                    if (item == null) continue;
                    object sectVal = _contentSectionProp.GetValue(item, null);
                    if (sectVal == null ||
                        !string.Equals(sectVal.ToString(), section, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Section matched. Only SingleClip exposes a Clip; ClipSet etc. are unsupported.
                    if (!_singleClipClipProp.DeclaringType.IsInstanceOfType(item))
                        return false;

                    bool isLoaded = _contentIsLoadedProp != null
                        && _contentIsLoadedProp.GetValue(item, null) is bool b && b;
                    AudioClip c = _singleClipClipProp.GetValue(item, null) as AudioClip;

                    if (isLoaded && c != null)
                    {
                        _vanillaClipReady[section] = c;
                        clip = c;
                        return true;
                    }

                    // Not loaded yet — kick a one-time load so a later tick can use it.
                    if (!_vanillaLoadKicked.Contains(section) && _singleClipLoadMethod != null
                        && GameManager.Instance != null)
                    {
                        _vanillaLoadKicked.Add(section);
                        try
                        {
                            if (_singleClipLoadMethod.Invoke(item, null) is IEnumerator routine)
                            {
                                GameManager.Instance.StartCoroutine(routine);
                                CMMConfig.D($"[CMM] Kicked vanilla clip load for {section}.");
                            }
                        }
                        catch (Exception ex)
                        { Log.Warning($"[CMM] Vanilla clip Load() kick failed for {section}: {ex.Message}"); }
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] TryGetReadyVanillaClip error for {section}: {ex.Message}");
            }
            return false;
        }
    }
}
