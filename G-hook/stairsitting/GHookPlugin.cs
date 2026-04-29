using Minecraft.Server.FourKit;
using Minecraft.Server.FourKit.Plugin;
using Minecraft.Server.FourKit.Command;

public class GHookPlugin : ServerPlugin
{
    public override string name => "G-Hook";
    public override string version => "1.0.0";
    public override string author => "Bobby";

    public override void onEnable()
    {
        FourKit.addListener(new GHookListener());
        FourKit.getCommand("ghook").setExecutor(new GHookCommand());
        FourKit.getCommand("ghook").setDescription("Gives a grappling hook item.");
        FourKit.getCommand("ghook").setUsage("/ghook give [player]");
    }

    public override void onDisable() { }
}
