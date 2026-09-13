using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
    public sealed class SubModule : MBSubModuleBase
    {
        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            RescueService.AutoTick();
        }
    }

    public static class RescueCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("fix", "rescue")]
        public static string Fix(List<string> args)
        {
            return RescueService.RunFix(true);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("status", "rescue")]
        public static string Status(List<string> args)
        {
            return RescueService.GetStatus();
        }
    }

    internal static class RescueService
    {
        private const string HeroId = "lord_7_5_2";
        private const string ActionName = "follow_player";
        private static DateTime _nextAttempt = DateTime.MinValue;
        private static DateTime? _campaignSeenAt;
        private static int _attempts;
        private static string _lastResult = "Not run yet.";

        public static void AutoTick()
        {
            if (DateTime.UtcNow < _nextAttempt)
                return;

            _nextAttempt = DateTime.UtcNow.AddMilliseconds(250);

            if (!IsCampaignReady())
                return;

            if (!_campaignSeenAt.HasValue)
            {
                _campaignSeenAt = DateTime.UtcNow;
                Log("Campaign detected. Starting automatic rescue window.");
            }

            // Keep enforcing the repaired state for 30 seconds after load so that
            // AI Influence cannot restore follow_player after our first pass.
            if ((DateTime.UtcNow - _campaignSeenAt.Value).TotalSeconds > 30.0)
                return;

            _attempts++;
            _lastResult = RunFix(false);
        }

        public static string RunFix(bool verbose)
        {
            try
            {
                object hero = FindHero(HeroId);
                if (hero == null)
                    return Finish("Aelfeyja not found yet; will retry.", verbose);

                var notes = new List<string>();
                notes.Add("Aelfeyja found (" + HeroId + ").");

                bool stopped = TryStopAIInfluenceAction(hero, notes);
                bool detached = RepairPartyState(hero, notes);

                string result = "RESCUE PASS: StopAction=" + stopped + ", PartyRepair=" + detached + ". " + string.Join(" ", notes);
                return Finish(result, verbose);
            }
            catch (Exception ex)
            {
                return Finish("RESCUE ERROR: " + Flatten(ex), verbose);
            }
        }

        public static string GetStatus()
        {
            try
            {
                if (!IsCampaignReady())
                    return "Campaign is not ready. Last rescue result: " + _lastResult;

                object hero = FindHero(HeroId);
                if (hero == null)
                    return "Aelfeyja (" + HeroId + ") was not found. Last rescue result: " + _lastResult;

                object party = GetProperty(hero, "PartyBelongedTo");
                if (party == null)
                    return "Aelfeyja found; PartyBelongedTo=null. Last rescue result: " + _lastResult;

                object mapEvent = GetProperty(party, "MapEvent");
                object mapEventSide = GetProperty(party, "MapEventSide");
                object ai = GetProperty(party, "Ai");
                object targetParty = ai == null ? null : GetProperty(ai, "MoveTargetParty");

                return "Aelfeyja status: Party=" + SafeName(party)
                    + ", MapEvent=" + (mapEvent == null ? "null" : SafeName(mapEvent))
                    + ", MapEventSide=" + (mapEventSide == null ? "null" : SafeName(mapEventSide))
                    + ", MoveTargetParty=" + (targetParty == null ? "null" : SafeName(targetParty))
                    + ". Last rescue result: " + _lastResult;
            }
            catch (Exception ex)
            {
                return "STATUS ERROR: " + Flatten(ex);
            }
        }

        private static bool IsCampaignReady()
        {
            Type campaignType = FindType("TaleWorlds.CampaignSystem.Campaign");
            if (campaignType == null)
                return false;

            PropertyInfo current = campaignType.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return current != null && current.GetValue(null, null) != null;
        }

        private static object FindHero(string stringId)
        {
            Type campaignType = FindType("TaleWorlds.CampaignSystem.Campaign");
            if (campaignType == null)
                return null;

            PropertyInfo currentProp = campaignType.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object campaign = currentProp == null ? null : currentProp.GetValue(null, null);
            if (campaign == null)
                return null;

            foreach (string propertyName in new[] { "AliveHeroes", "Heroes" })
            {
                object heroes = GetProperty(campaign, propertyName);
                object found = FindHeroInEnumerable(heroes as IEnumerable, stringId);
                if (found != null)
                    return found;
            }

            Type heroType = FindType("TaleWorlds.CampaignSystem.Hero");
            if (heroType != null)
            {
                foreach (string propertyName in new[] { "AllAliveHeroes", "All" })
                {
                    PropertyInfo p = heroType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    object heroes = p == null ? null : p.GetValue(null, null);
                    object found = FindHeroInEnumerable(heroes as IEnumerable, stringId);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private static object FindHeroInEnumerable(IEnumerable heroes, string stringId)
        {
            if (heroes == null)
                return null;

            foreach (object hero in heroes)
            {
                if (hero == null)
                    continue;

                object id = GetProperty(hero, "StringId");
                if (id != null && string.Equals(id.ToString(), stringId, StringComparison.Ordinal))
                    return hero;
            }
            return null;
        }

        private static bool TryStopAIInfluenceAction(object hero, List<string> notes)
        {
            try
            {
                Type managerType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(SafeGetTypes)
                    .FirstOrDefault(t => t != null && t.Name == "AIActionManager" && t.FullName != null && t.FullName.IndexOf("AIInfluence", StringComparison.OrdinalIgnoreCase) >= 0);

                if (managerType == null)
                {
                    notes.Add("AIActionManager not loaded yet.");
                    return false;
                }

                object manager = GetStaticMember(managerType, "Instance")
                    ?? GetStaticMember(managerType, "_instance")
                    ?? GetStaticMember(managerType, "instance");

                MethodInfo[] methods = managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => m.Name == "StopAction")
                    .ToArray();

                foreach (MethodInfo method in methods)
                {
                    ParameterInfo[] ps = method.GetParameters();
                    object[] args = new object[ps.Length];
                    bool heroAssigned = false;
                    bool actionAssigned = false;
                    bool compatible = true;

                    for (int i = 0; i < ps.Length; i++)
                    {
                        Type pt = ps[i].ParameterType;
                        if (!heroAssigned && pt.IsInstanceOfType(hero))
                        {
                            args[i] = hero;
                            heroAssigned = true;
                        }
                        else if (!actionAssigned && pt == typeof(string))
                        {
                            args[i] = ActionName;
                            actionAssigned = true;
                        }
                        else if (pt == typeof(bool))
                        {
                            args[i] = true;
                        }
                        else if (ps[i].HasDefaultValue)
                        {
                            args[i] = ps[i].DefaultValue;
                        }
                        else if (!pt.IsValueType)
                        {
                            args[i] = null;
                        }
                        else
                        {
                            compatible = false;
                            break;
                        }
                    }

                    if (!compatible || !heroAssigned || !actionAssigned)
                        continue;

                    if (!method.IsStatic && manager == null)
                        continue;

                    method.Invoke(method.IsStatic ? null : manager, args);
                    notes.Add("AIInfluence StopAction(follow_player) invoked.");
                    return true;
                }

                notes.Add("StopAction method was found but no compatible overload could be invoked.");
                return false;
            }
            catch (Exception ex)
            {
                notes.Add("StopAction failed: " + Flatten(ex));
                return false;
            }
        }

        private static bool RepairPartyState(object hero, List<string> notes)
        {
            object party = GetProperty(hero, "PartyBelongedTo");
            if (party == null)
            {
                notes.Add("Aelfeyja has no MobileParty; nothing to detach.");
                return true;
            }

            bool changed = false;

            // A follower forcibly injected into a battle can be left with a stale MapEventSide.
            // Bannerlord itself clears MapEventSide before destructive party actions, so nulling
            // a stale side after the battle is the least invasive repair available.
            object mapEvent = GetProperty(party, "MapEvent");
            object mapEventSide = GetProperty(party, "MapEventSide");
            if (mapEvent != null || mapEventSide != null)
            {
                if (SetProperty(party, "MapEventSide", null))
                {
                    notes.Add("Cleared stale MapEventSide.");
                    changed = true;
                }
                else
                {
                    notes.Add("WARNING: MapEvent is still present and MapEventSide could not be cleared.");
                }
            }
            else
            {
                notes.Add("MapEvent/MapEventSide already clear.");
            }

            object ai = GetProperty(party, "Ai");
            if (ai != null)
            {
                if (InvokeNamed(ai, "SetDoNotMakeNewDecisions", new object[] { false }))
                {
                    notes.Add("Re-enabled normal party AI decisions.");
                    changed = true;
                }

                if (InvokeNamed(ai, "SetMoveModeHold", new object[0]))
                {
                    notes.Add("Set party movement to Hold to break stale escort targeting.");
                    changed = true;
                }
            }
            else
            {
                notes.Add("Party Ai object was null.");
            }

            return changed || (mapEvent == null && mapEventSide == null);
        }

        private static bool InvokeNamed(object target, string name, object[] args)
        {
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == name && m.GetParameters().Length == args.Length)
                .ToArray();

            foreach (MethodInfo method in methods)
            {
                try
                {
                    method.Invoke(target, args);
                    return true;
                }
                catch
                {
                    // Try another overload.
                }
            }
            return false;
        }

        private static object GetStaticMember(Type type, string name)
        {
            PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (p != null)
            {
                try { return p.GetValue(null, null); } catch { }
            }

            FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f != null)
            {
                try { return f.GetValue(null); } catch { }
            }
            return null;
        }

        private static object GetProperty(object obj, string name)
        {
            if (obj == null)
                return null;
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (p == null)
                return null;
            try { return p.GetValue(obj, null); } catch { return null; }
        }

        private static bool SetProperty(object obj, string name, object value)
        {
            if (obj == null)
                return false;
            PropertyInfo p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p == null)
                return false;
            MethodInfo setter = p.GetSetMethod(true);
            if (setter == null)
                return false;
            try
            {
                setter.Invoke(obj, new[] { value });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false);
                    if (type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Enumerable.Empty<Type>(); }
        }

        private static string SafeName(object obj)
        {
            if (obj == null)
                return "null";
            try
            {
                object name = GetProperty(obj, "Name");
                return name == null ? obj.GetType().Name : name.ToString();
            }
            catch
            {
                return obj.GetType().Name;
            }
        }

        private static string Finish(string message, bool verbose)
        {
            _lastResult = message;
            Log(message);
            return message;
        }

        private static string Flatten(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null)
                ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static void Log(string message)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string root = Directory.GetParent(baseDir)?.Parent?.FullName ?? baseDir;
                string moduleDir = Path.Combine(root, "Modules", "AelfeyjaRescue");
                Directory.CreateDirectory(moduleDir);
                File.AppendAllText(Path.Combine(moduleDir, "rescue.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
            }
            catch
            {
                // Never allow logging to affect the rescue operation.
            }
        }
    }
}
