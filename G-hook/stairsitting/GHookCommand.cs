using Minecraft.Server.FourKit;
using Minecraft.Server.FourKit.Command;
using Minecraft.Server.FourKit.Entity;
using System;
using System.Linq;

namespace GrapplingHook
{
    public class GHookCommand : CommandExecutor
    {
        public bool onCommand(CommandSender sender, Command command, string label, string[] args)
        {
            if (args.Length == 0 || !args[0].Equals("give", StringComparison.OrdinalIgnoreCase))
            {
                sender.sendMessage("Usage: /ghook give [player]");
                return true;
            }

            Player? target = null;

            if (args.Length >= 2)
            {
                string name = args[1];
                target = FourKit.getOnlinePlayers()
                    .FirstOrDefault(p => p.getName().Equals(name, StringComparison.OrdinalIgnoreCase));

                if (target == null)
                {
                    sender.sendMessage($"Player '{name}' not found or not online.");
                    return true;
                }
            }
            else if (sender is Player senderPlayer)
            {
                target = senderPlayer;
            }
            else
            {
                sender.sendMessage("Specify a player name when running from console.");
                return true;
            }

            target.getInventory().addItem(GHookListener.CreateGrapplingHook());
            target.sendMessage($"{ChatColor.WHITE}You received a {ChatColor.LIGHT_PURPLE}Grappling Hook{ChatColor.WHITE}!");

            if (sender is Player sp && sp.getUniqueId() != target.getUniqueId())
                sender.sendMessage($"Gave Grappling Hook to {target.getName()}.");

            return true;
        }
    }
}
