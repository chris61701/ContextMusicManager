
using System.Collections.Generic;

namespace CMM
{
    // In-game console command: `cmm rescan <context|all>`.
    //
    // The recursive crawler (RecursiveCrawlLoop) is the live updater for subfolder
    // contexts; this command is the manual force-refresh — it discards a context's
    // cached directory state so the next sweep re-lists every folder from scratch.
    // Useful after bulk-adding files on a filesystem that doesn't reliably bump
    // folder mtimes (some network shares / removable media), or to refresh sooner
    // than the next sweep would.
    //
    // 7 Days to Die auto-discovers ConsoleCmdAbstract subclasses in loaded mod
    // assemblies, so simply defining this class registers the command.
    //
    // The base class shape differs across versions: A20 exposes PascalCase abstract
    // GetCommands/GetDescription; A21/2.6/3.0 expose camelCase protected
    // getCommands/getDescription. Execute(List<string>, CommandSenderInfo) is common.
    public class CmmConsoleCmd : ConsoleCmdAbstract
    {
        private static readonly string[] Names = { "cmm" };
        private const string Desc = "Context Music Manager controls";
        private const string Help =
            "Usage:\n" +
            "  cmm rescan <context>  - re-scan one subfolder context now\n" +
            "  cmm rescan all        - re-scan every subfolder context now\n" +
            "  cmm rescan            - same as 'all'";

#if GV_A20
        // A20: PascalCase, public abstract.
        public override string[] GetCommands() => Names;
        public override string GetDescription() => Desc;
        public override string GetHelp() => Help;
#elif GV_A21
        // A21: camelCase, and its referenced Assembly-CSharp keeps them protected.
        protected override string[] getCommands() => Names;
        protected override string getDescription() => Desc;
        protected override string getHelp() => Help;
#else
        // 2.6 / 3.0: camelCase, publicized to public in the referenced Assembly-CSharp.
        public override string[] getCommands() => Names;
        public override string getDescription() => Desc;
        public override string getHelp() => Help;
#endif

        public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
        {
            if (_params == null || _params.Count == 0) { Out(Help); return; }

            string sub = _params[0].ToLowerInvariant();
            if (sub == "rescan")
            {
                string target = _params.Count > 1 ? _params[1] : "all";
                Out(ContextMusicManager.RescanCommand(target));
                return;
            }

            Out("unknown subcommand '" + _params[0] + "'.\n" + Help);
        }

        private static void Out(string s)
        {
            Log.Out("[CMM] " + s.Replace("\n", "\n[CMM] "));
        }
    }
}
