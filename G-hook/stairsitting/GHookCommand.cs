using Minecraft.Server.FourKit.Command;
using Minecraft.Server.FourKit.Entity;

public class GHookCommand : CommandExecutor
{
    private readonly GHookListener _listener;

    public GHookCommand(GHookListener listener)
    {
        _listener = listener;
    }

    public bool onCommand(CommandSender sender, Command command, string label, string[] args)
    {
        if (sender is ConsoleCommandSender)
        {
            sender.sendMessage("headass.");
            return true;
        }

        Player p = (Player)sender;
        bool isNowEnabled = _listener.toggleGrapple(p.getUniqueId());

        if (isNowEnabled)
            p.sendMessage("G-Hook enabled.");
        else
            p.sendMessage("G-Hook disabled.");

        return true;
    }
}