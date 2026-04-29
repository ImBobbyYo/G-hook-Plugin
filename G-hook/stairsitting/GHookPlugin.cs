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
        var listener = new GHookListener();
        FourKit.addListener(listener);

        FourKit.getCommand("ghook").setExecutor(new GHookCommand(listener));
        FourKit.getCommand("ghook").setDescription("Turns grappling hook off or on. It's off by default");
        FourKit.getCommand("ghook").setUsage("/ghook");
    }

    public override void onDisable() { }
}