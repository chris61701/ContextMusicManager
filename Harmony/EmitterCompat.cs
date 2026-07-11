// Gated to 3.0 (GV_V3) like the rest of the world-emitter layer — but note this gate is about
// which BUILD contains the code, not which GAME runs it. The everyday Debug fan-out and the
// Release package both ship ONE DLL compiled against 3.0 (GV_V3 defined) to every supported
// game version, so all the GV_V3 code below is PRESENT and loadable on A20/A21/2.6. This class
// is the runtime side of the gate: it detects the game we actually woke up inside and lets the
// emitter feature refuse to run anywhere it wasn't built for.
#if GV_V3
using HarmonyLib;

namespace CMM
{
    internal static class EmitterCompat
    {
        // True only on a 3.0+ game (release-type "V", Major >= 3). Every world-emitter entry
        // point that can execute on an older game must check this FIRST:
        //   - EmitterAudioLayer.Tick() (the frame driver)
        //   - Patch_DoorAcousticChange.Prepare() (so the TEFeatureDoor patch is skipped, not applied)
        //   - the DebugHud "Emitters:" row (drives EmitterRegistry.Poll directly)
        // Everything deeper is safe by lazy JIT: methods that touch 3.0-shaped game APIs are
        // never invoked when this is false, so their member tokens are never resolved.
        internal static readonly bool RuntimeSupported = Probe();

        static bool Probe()
        {
            try
            {
                // Constants.cVersionInformation and VersionInformation.{ReleaseType,Major} have the
                // same shape (public readonly fields) from A20 through 3.0, but bind by NAME anyway:
                // this probe is the one piece of emitter code guaranteed to RUN on every version, so
                // it must not carry a hard member token that could itself fail to resolve.
                object vi = AccessTools.Field(AccessTools.TypeByName("Constants"), "cVersionInformation")?.GetValue(null);
                if (vi == null) return false;
                var t = vi.GetType();
                object rel = AccessTools.Field(t, "ReleaseType")?.GetValue(vi);
                object major = AccessTools.Field(t, "Major")?.GetValue(vi);
                // A20/A21 report "Alpha 20/21"; 2.x/3.x report "V 2/3". The TEFeatureDoor probe is a
                // belt-and-braces capability check that the door hook's patch target really exists.
                return rel != null && rel.ToString() == "V"
                    && major is int m && m >= 3
                    && AccessTools.TypeByName("TEFeatureDoor") != null;
            }
            catch
            {
                // Unknown game shape → treat as unsupported: the radio stays off, the rest of
                // the mod is untouched.
                return false;
            }
        }
    }
}
#endif
