using Minecraft.Server.FourKit;
using Minecraft.Server.FourKit.Event;
using Minecraft.Server.FourKit.Event.Player;
using Minecraft.Server.FourKit.Event.Entity;
using Minecraft.Server.FourKit.Entity;
using Minecraft.Server.FourKit.Inventory;
using Minecraft.Server.FourKit.Inventory.Meta;
using Minecraft.Server.FourKit.Util;
using System;
using System.Collections.Generic;
using System.Threading;
using FKAction = Minecraft.Server.FourKit.Block.Action;

namespace GrapplingHook
{
    public class GHookListener : Listener
    {
        public const string HOOK_NAME = "Grappling Hook";

        private const long FALLDMG_WINDOW = 5000;
        private const double LAUNCH = 1.5;
        private const double INERTIA = 0.92;
        private const double GRAV = 0.04;
        private const int MAX_TICKS = 200;
        private const int TICK_MS = 50;
        private const double PULL_GRAV = -0.08;
        private const double MAX_LINE_DIST_SQ = 32.0 * 32.0;
        private const double VELOCITY_STOP = 0.001;

        private enum HookState { Idle, Cast }

        private readonly Dictionary<Guid, HookState> state = [];
        private readonly Dictionary<Guid, long> lastGrapple = [];
        private readonly Dictionary<Guid, List<(double x, double y, double z)>> bobberPath = [];
        private readonly Dictionary<Guid, int> bobberIdx = [];

        private CancellationTokenSource? loopCts;

        private HookState GetState(Guid uid)
            => state.TryGetValue(uid, out var s) ? s : HookState.Idle;

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static bool IsGrapplingHook(ItemStack? item)
        {
            if (item == null || item.getType() != Material.FISHING_ROD) return false;
            if (!item.hasItemMeta()) return false;
            var meta = item.getItemMeta();
            return meta.hasDisplayName() && meta.getDisplayName() == HOOK_NAME;
        }

        public static ItemStack CreateGrapplingHook()
        {
            var item = new ItemStack(Material.FISHING_ROD, 1);
            var meta = item.getItemMeta();
            meta.setDisplayName(HOOK_NAME);
            meta.setLore(["The hook will bring you back!", "I ain't tellin you no lie!"]);
            item.setItemMeta(meta);
            return item;
        }

        private void EnsureLoop()
        {
            if (loopCts != null) return;
            loopCts = new CancellationTokenSource();
            var token = loopCts.Token;
            new Thread(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        Thread.Sleep(TICK_MS);
                        lock (bobberIdx)
                        {
                            if (bobberIdx.Count == 0) continue;
                            foreach (var uid in new List<Guid>(bobberIdx.Keys))
                            {
                                if (!bobberIdx.TryGetValue(uid, out int idx) || !bobberPath.TryGetValue(uid, out var bPath)) continue;
                                if (idx < bPath.Count - 1)
                                    bobberIdx[uid] = idx + 1;
                            }
                        }
                    }
                }
                catch { }
            })
            { IsBackground = true }.Start();
        }

        private void ResetCast(Guid uid)
        {
            state.Remove(uid);
            lock (bobberIdx)
            {
                bobberPath.Remove(uid);
                bobberIdx.Remove(uid);
            }
        }

        private void StartBobber(Guid uid, World world, double sx, double sy, double sz, float yawDeg, float pitchDeg)
        {
            ResetCast(uid);

            double yr = yawDeg * (Math.PI / 180.0);
            double xr = pitchDeg * (Math.PI / 180.0);

            double px = sx - Math.Cos(yr) * 0.16;
            double py = sy - 0.1;
            double pz = sz - Math.Sin(yr) * 0.16;

            double vx = -Math.Sin(yr) * Math.Cos(xr) * LAUNCH;
            double vy = -Math.Sin(xr) * LAUNCH;
            double vz = Math.Cos(yr) * Math.Cos(xr) * LAUNCH;

            var path = new List<(double x, double y, double z)>();
            for (int i = 0; i < MAX_TICKS; i++)
            {
                double prevPx = px;
                double prevPy = py;
                double prevPz = pz;
                vy -= GRAV;
                px += vx; py += vy; pz += vz;
                vx *= INERTIA; vy *= INERTIA; vz *= INERTIA;

                double ddx = px - sx, ddy = py - sy, ddz = pz - sz;
                if (ddx * ddx + ddy * ddy + ddz * ddz > MAX_LINE_DIST_SQ) break;

                int bx = (int)Math.Floor(px);
                int by = (int)Math.Floor(py);
                int bz = (int)Math.Floor(pz);

                if (world.getBlockTypeIdAt(bx, by, bz) != 0)
                {
                    bool crossedX = (int)Math.Floor(prevPx) != bx;
                    bool crossedY = (int)Math.Floor(prevPy) != by;
                    bool crossedZ = (int)Math.Floor(prevPz) != bz;

                    if (crossedX) vx = 0;
                    if (crossedY) vy = 0;
                    if (crossedZ) vz = 0;
                    if (!crossedX && !crossedY && !crossedZ) { vx = 0; vy = 0; vz = 0; }

                    px = prevPx; py = prevPy; pz = prevPz;
                    path.Add((px, py, pz));

                    vx *= 0.5; vy *= 0.5; vz *= 0.5;

                    if (Math.Abs(vx) + Math.Abs(vy) + Math.Abs(vz) < VELOCITY_STOP) break;
                    continue;
                }

                path.Add((px, py, pz));

                if (Math.Abs(vx) + Math.Abs(vy) + Math.Abs(vz) < VELOCITY_STOP) break;
                if (py < sy - 64) break;
            }

            lock (bobberIdx)
            {
                bobberPath[uid] = path;
                bobberIdx[uid] = 0;
            }
            EnsureLoop();
        }

        private (double x, double y, double z)? GetBobberPos(Guid uid)
        {
            lock (bobberIdx)
            {
                if (!bobberPath.TryGetValue(uid, out var path) || !bobberIdx.TryGetValue(uid, out int idx))
                    return null;
                return path[Math.Min(idx, path.Count - 1)];
            }
        }

        private static void PullPlayerToLocation(Player player, double destX, double destY, double destZ,
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
        public void OnInteract(PlayerInteractEvent e)
        {
            try
            {
                if (e.getAction() != FKAction.RIGHT_CLICK_AIR) return;

                Player p = e.getPlayer();
                Guid uid = p.getUniqueId();

                if (!IsGrapplingHook(e.hasItem() ? p.getItemInHand() : null)) return;

                if (GetState(uid) == HookState.Idle)
                {
                    var loc = p.getLocation();
                    StartBobber(uid, p.getWorld(), loc.getX(), loc.getY() + 1.62, loc.getZ(), loc.getYaw(), loc.getPitch());
                    state[uid] = HookState.Cast;
                    return;
                }

                var bobber = GetBobberPos(uid);
                ResetCast(uid);

                if (!bobber.HasValue) return;

                double bx = bobber.Value.x, by = bobber.Value.y, bz = bobber.Value.z;
                int ix = (int)Math.Floor(bx);
                int iy = (int)Math.Floor(by);
                int iz = (int)Math.Floor(bz);
                var world = p.getWorld();

                (double x, double y, double z)? contact = null;

                if (world.getBlockTypeIdAt(ix, iy - 1, iz) != 0)
                    contact = (bx, iy, bz);
                else if (world.getBlockTypeIdAt(ix, iy + 1, iz) != 0)
                    contact = (bx, iy + 1.0, bz);
                else if (world.getBlockTypeIdAt(ix + 1, iy, iz) != 0)
                    contact = (ix + 1.0, by, bz);
                else if (world.getBlockTypeIdAt(ix - 1, iy, iz) != 0)
                    contact = (ix, by, bz);
                else if (world.getBlockTypeIdAt(ix, iy, iz + 1) != 0)
                    contact = (bx, by, iz + 1.0);
                else if (world.getBlockTypeIdAt(ix, iy, iz - 1) != 0)
                    contact = (bx, by, iz);

                if (contact == null) return;

                var ploc = p.getLocation();
                lastGrapple[uid] = Now();
                p.playSound(ploc, Sound.PISTON_RETRACT, 0.8f, 1.2f);
                PullPlayerToLocation(p, contact.Value.x, contact.Value.y, contact.Value.z,
                                     ploc.getX(), ploc.getY(), ploc.getZ());
            }
            catch (Exception ex) { _ = ex; }
        }

        [EventHandler]
        public void OnMove(PlayerMoveEvent e)
        {
            try
            {
                Player p = e.getPlayer();
                Guid uid = p.getUniqueId();

                if (GetState(uid) != HookState.Cast) return;

                if (!IsGrapplingHook(p.getItemInHand()))
                {
                    ResetCast(uid);
                    return;
                }

                var bobber = GetBobberPos(uid);
                if (bobber.HasValue)
                {
                    var loc = p.getLocation();
                    double dx = bobber.Value.x - loc.getX();
                    double dy = bobber.Value.y - loc.getY();
                    double dz = bobber.Value.z - loc.getZ();
                    if (dx * dx + dy * dy + dz * dz > MAX_LINE_DIST_SQ)
                        ResetCast(uid);
                }
            }
            catch (Exception ex) { _ = ex; }
        }

        [EventHandler]
        public void OnDeath(PlayerDeathEvent e)
        {
            ResetCast(((Player)e.getEntity()).getUniqueId());
        }

        [EventHandler]
        public void OnDamage(EntityDamageEvent e)
        {
            try
            {
                if (e.getEntityType() != EntityType.PLAYER) return;
                if (e.getCause() != EntityDamageEvent.DamageCause.FALL) return;

                Player p = (Player)e.getEntity();
                Guid uid = p.getUniqueId();

                if (lastGrapple.TryGetValue(uid, out long t) && Now() - t < FALLDMG_WINDOW)
                    e.setCancelled(true);
            }
            catch (Exception ex) { _ = ex; }
        }

        [EventHandler]
        public void OnQuit(PlayerQuitEvent e)
        {
            Guid uid = e.getPlayer().getUniqueId();
            ResetCast(uid);
            lastGrapple.Remove(uid);
        }
    }
}
