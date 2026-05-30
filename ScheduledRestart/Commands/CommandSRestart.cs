using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace ScheduledRestart
{
    /// <summary>
    /// In-game admin control, mirroring the Discord /restart commands:
    ///   /srestart            -> show the next scheduled restart
    ///   /srestart now        -> save + restart immediately
    ///   /srestart &lt;minutes&gt;  -> emergency restart in N minutes (with countdown)
    ///   /srestart cancel     -> cancel an emergency restart
    /// </summary>
    public sealed class CommandSRestart : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "srestart";
        public string Help => "Schedule or cancel a server restart";
        public string Syntax => "<now|cancel|minutes>";
        public List<string> Aliases => new List<string> { "serverrestart" };
        public List<string> Permissions => new List<string> { "scheduledrestart.admin" };

        public void Execute(IRocketPlayer caller, string[] args)
        {
            ScheduledRestartPlugin plugin = ScheduledRestartPlugin.Instance;
            if (plugin == null)
            {
                UnturnedChat.Say(caller, "ScheduledRestart is not loaded.", Color.red);
                return;
            }

            if (args.Length == 0)
            {
                UnturnedChat.Say(caller, "ใช้: /srestart <now|cancel|นาที> | Usage: /srestart <now|cancel|minutes>", Color.yellow);
                return;
            }

            string arg = args[0].ToLowerInvariant();

            if (arg == "cancel")
            {
                plugin.ApplyCancel();
                UnturnedChat.Say(caller, "ยกเลิกการรีสตาร์ทฉุกเฉินแล้ว | Emergency restart cancelled.", Color.green);
                return;
            }

            if (arg == "now")
            {
                plugin.ApplyEmergency(0);
                UnturnedChat.Say(caller, "กำลังรีสตาร์ททันที (บันทึกโลกก่อน) | Restarting now (saving first).", Color.green);
                return;
            }

            if (int.TryParse(arg, out int minutes) && minutes > 0)
            {
                plugin.ApplyEmergency(minutes);
                UnturnedChat.Say(caller, "ตั้งรีสตาร์ทในอีก " + minutes + " นาที | Restart scheduled in " + minutes + " min.", Color.green);
                return;
            }

            UnturnedChat.Say(caller, "ค่าไม่ถูกต้อง ใช้: /srestart <now|cancel|นาที> | Invalid. Use /srestart <now|cancel|minutes>", Color.red);
        }
    }
}
