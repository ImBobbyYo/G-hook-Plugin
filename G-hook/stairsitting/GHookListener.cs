using Minecraft.Server.FourKit;
using Minecraft.Server.FourKit.Event;
using Minecraft.Server.FourKit.Event.Player;
using Minecraft.Server.FourKit.Event.Entity;
using Minecraft.Server.FourKit.Entity;
using Minecraft.Server.FourKit.Util;
using System;
using System.Collections.Generic;
using System.Threading;
using FKAction = Minecraft.Server.FourKit.Block.Action;

public class GHookListener : Listener
{
    private const long FALLDMG_WINDOW = 5000;
    private const double LAUNCH = 1.5;
    private const double INERTIA = 0.92;
    private const double GRAV = 0.04;
    private const int MAX_TICKS = 200;
    private const int TICK_MS = 50;
    private const double PULL_GRAV = -0.08;
    private const double MAX_LINE_DIST_SQ = 32.0 * 32.0;

    private enum HookState { Idle, Cast }

    HashSet<Guid> enabledPlayers = new();
    Dictionary<Guid, HookState> state = new();
    Dictionary<Guid, long> lastGrapple = new();
    Dictionary<Guid, List<(double x, double y, double z)>> bobberPath = new();
    Dictionary<Guid, int> bobberIdx = new();
    Dictionary<Guid, int> bobberFinalIdx = new();
    Dictionary<Guid, CancellationTokenSource> threads = new();

    private HookState getState(Guid uid) => state.TryGetValue(uid, out var s) ? s : HookState.Idle;
    private long now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void killThread(Guid uid)
    {
        if (threads.TryGetValue(uid, out var cts)) { cts.Cancel(); threads.Remove(uid); }
    }

    private void resetCast(Guid uid)
    {
        killThread(uid);
        state.Remove(uid);
        bobberPath.Remove(uid);
        bobberIdx.Remove(uid);
        bobberFinalIdx.Remove(uid);
    }

    public bool toggleGrapple(Guid uid)
    {
        if (enabledPlayers.Remove(uid)) { resetCast(uid); return false; }
        enabledPlayers.Add(uid);
        return true;
    }

    private void startBobber(Guid uid, World world, double sx, double sy, double sz, float yawDeg, float pitchDeg)
    {
        killThread(uid);

        double yr = yawDeg * (Math.PI / 180.0);
        double xr = pitchDeg * (Math.PI / 180.0);

        double px = sx - Math.Cos(yr) * 0.16;
        double py = sy - 0.1;
        double pz = sz - Math.Sin(yr) * 0.16;

        double vx = -Math.Sin(yr) * Math.Cos(xr) * LAUNCH;
        double vy = -Math.Sin(xr) * LAUNCH;
        double vz = Math.Cos(yr) * Math.Cos(xr) * LAUNCH;

        var path = new List<(double x, double y, double z)>();
        int finalIdx = -1;

        double prevPx = px, prevPy = py, prevPz = pz;

        for (int i = 0; i < MAX_TICKS; i++)
        {
            prevPx = px; prevPy = py; prevPz = pz;

            vy -= GRAV;
            px += vx; py += vy; pz += vz;
            vx *= INERTIA; vy *= INERTIA; vz *= INERTIA;

            // Line snap distance check 
            double ddx = px - sx, ddy = py - sy, ddz = pz - sz;
            if (ddx * ddx + ddy * ddy + ddz * ddz > MAX_LINE_DIST_SQ) break;

            int bx = (int)Math.Floor(px);
            int by = (int)Math.Floor(py);
            int bz = (int)Math.Floor(pz);

            if (world.getBlockTypeIdAt(bx, by, bz) != 0)
            {
                if (prevPy >= by)
                {
                    double hitY = by + 1.0;
                    double stepY = py - prevPy;
                    double lerp = Math.Abs(stepY) > 0.0001 ? Math.Clamp((hitY - prevPy) / stepY, 0.0, 1.0) : 0.0;
                    path.Add((prevPx + lerp * (px - prevPx), hitY, prevPz + lerp * (pz - prevPz)));
                    finalIdx = path.Count - 1;
                    break;
                }
            }

            path.Add((px, py, pz));
            if (py < sy - 64) break;
        }

        bobberPath[uid] = path;
        bobberIdx[uid] = 0;
        bobberFinalIdx[uid] = finalIdx;

        var cts = new CancellationTokenSource();
        threads[uid] = cts;
        var token = cts.Token;

        new Thread(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Thread.Sleep(TICK_MS);
                    lock (bobberIdx)
                    {
                        if (!bobberIdx.ContainsKey(uid) || !bobberPath.ContainsKey(uid)) return;
                        int idx = bobberIdx[uid];
                        if (idx < bobberPath[uid].Count - 1)
                            bobberIdx[uid] = idx + 1;
                    }
                }
            }
            catch { }
        })
        { IsBackground = true }.Start();
    }

    private (double x, double y, double z)? getBobberPos(Guid uid)
    {
        lock (bobberIdx)
        {
            if (!bobberPath.ContainsKey(uid) || !bobberIdx.ContainsKey(uid)) return null;
            var path = bobberPath[uid];
            return path[Math.Min(bobberIdx[uid], path.Count - 1)];
        }
    }

    private void pullPlayerToLocation(Player player, double destX, double destY, double destZ,
                                      double fromX, double fromY, double fromZ)
    {
        var boost = player.getVelocity();
        player.setVelocity(new Vector(boost.getX(), 0.3, boost.getZ()));

        new Thread(() =>
        {
            try
            {
                Thread.Sleep(TICK_MS);
                double dx = destX - fromX;
                double dy = destY - fromY;
                double dz = destZ - fromZ;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < 0.001) return;

                player.setVelocity(new Vector(
                    (1.0 + 0.07 * d) * (dx / d),
                    (1.0 + 0.03 * d) * (dy / d) - 0.5 * PULL_GRAV * d,
                    (1.0 + 0.07 * d) * (dz / d)
                ));
            }
            catch { }
        })
        { IsBackground = true }.Start();
    }

    [EventHandler]
    public void onInteract(PlayerInteractEvent e)
    {
        try
        {
            if (e.getAction() != FKAction.RIGHT_CLICK_AIR) return;
            if (!e.hasItem() || e.getMaterial() != Material.FISHING_ROD) return;

            Player p = e.getPlayer();
            Guid uid = p.getUniqueId();

            if (!enabledPlayers.Contains(uid)) return;

            if (getState(uid) == HookState.Idle)
            {
                var loc = p.getLocation();
                startBobber(uid, p.getWorld(), loc.getX(), loc.getY() + 1.62, loc.getZ(), loc.getYaw(), loc.getPitch());
                state[uid] = HookState.Cast;
                return;
            }

            if (!bobberFinalIdx.TryGetValue(uid, out int finalIdx) || finalIdx == -1 ||
                !bobberIdx.TryGetValue(uid, out int currentIdx) || currentIdx < finalIdx)
            {
                resetCast(uid);
                return;
            }

            var bobber = getBobberPos(uid);
            resetCast(uid);

            if (!bobber.HasValue) return;

            var ploc = p.getLocation();
            lastGrapple[uid] = now();
            p.playSound(ploc, Sound.ENDERMAN_TELEPORT, 1.0f, 1.5f);
            pullPlayerToLocation(p, bobber.Value.x, bobber.Value.y, bobber.Value.z,
                                 ploc.getX(), ploc.getY(), ploc.getZ());
        }
        catch (Exception) { }
    }

    [EventHandler]
    public void onMove(PlayerMoveEvent e)
    {
        try
        {
            Player p = e.getPlayer();
            Guid uid = p.getUniqueId();

            if (!enabledPlayers.Contains(uid)) return;
            if (getState(uid) != HookState.Cast) return;

            var item = p.getItemInHand();
            if (item == null || item.getType() != Material.FISHING_ROD)
            {
                resetCast(uid);
                return;
            }

            var bobber = getBobberPos(uid);
            if (bobber.HasValue)
            {
                var loc = p.getLocation();
                double dx = bobber.Value.x - loc.getX();
                double dy = bobber.Value.y - loc.getY();
                double dz = bobber.Value.z - loc.getZ();
                if (dx * dx + dy * dy + dz * dz > MAX_LINE_DIST_SQ)
                    resetCast(uid);
            }
        }
        catch (Exception) { }
    }

    [EventHandler]
    public void onDeath(PlayerDeathEvent e)
    {
        // hook snaps on death
        Guid uid = ((Player)e.getEntity()).getUniqueId();
        if (enabledPlayers.Contains(uid))
            resetCast(uid);
    }

    [EventHandler]
    public void onDamage(EntityDamageEvent e)
    {
        try
        {
            if (e.getEntityType() != EntityType.PLAYER) return;
            if (e.getCause() != EntityDamageEvent.DamageCause.FALL) return;

            Player p = (Player)e.getEntity();
            Guid uid = p.getUniqueId();

            if (lastGrapple.TryGetValue(uid, out long t) && now() - t < FALLDMG_WINDOW)
                e.setCancelled(true);
        }
        catch (Exception) { }
    }

    [EventHandler]
    public void onQuit(PlayerQuitEvent e)
    {
        Guid uid = e.getPlayer().getUniqueId();
        enabledPlayers.Remove(uid);
        resetCast(uid);
        lastGrapple.Remove(uid);
    }
}