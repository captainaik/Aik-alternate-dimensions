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
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			RescueService.LogPublic("Aelfeyja Rescue loaded. No automatic changes will be made. Use rescue.status / rescue.stop_follow / rescue.fix / rescue.hardfix.");
		}
	}

	public static class RescueCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("status", "rescue")]
		public static string Status(List<string> args)
		{
			return RescueService.GetStatus();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("stop_follow", "rescue")]
		public static string StopFollow(List<string> args)
		{
			return RescueService.StopFollowOnly();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("fix", "rescue")]
		public static string Fix(List<string> args)
		{
			return RescueService.ConservativeFix();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("hardfix", "rescue")]
		public static string HardFix(List<string> args)
		{
			return RescueService.HardFix();
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("destroy_party", "rescue")]
		public static string DestroyParty(List<string> args)
		{
			return RescueService.DestroyPartyOnly();
		}
	}

	internal static class RescueService
	{
		private const string HeroId = "lord_7_5_2";
		private const string ActionName = "follow_player";
		private static string _lastResult = "Not run yet.";

		public static string GetStatus()
		{
			try
			{
				if (!IsCampaignReady())
					return Finish("Campaign is not ready yet.");

				object hero = FindHero(HeroId);
				if (hero == null)
					return Finish("Aelfeyja (" + HeroId + ") was not found.");

				object party = GetProperty(hero, "PartyBelongedTo");
				object state = GetProperty(hero, "HeroState") ?? GetProperty(hero, "State");
				if (party == null)
					return Finish("Aelfeyja found. HeroState=" + SafeValue(state) + ", PartyBelongedTo=null. Last=" + _lastResult);

				object mapEvent = GetProperty(party, "MapEvent");
				object mapEventSide = GetProperty(party, "MapEventSide");
				object ai = GetProperty(party, "Ai");
				object targetParty = ai == null ? null : GetProperty(ai, "MoveTargetParty");

				return Finish("Aelfeyja status: HeroState=" + SafeValue(state)
					+ ", Party=" + SafeName(party)
					+ ", IsActive=" + SafeValue(GetProperty(party, "IsActive"))
					+ ", MapEvent=" + SafeName(mapEvent)
					+ ", MapEventSide=" + SafeName(mapEventSide)
					+ ", MoveTargetParty=" + SafeName(targetParty));
			}
			catch (Exception ex)
			{
				return Finish("STATUS ERROR: " + Flatten(ex));
			}
		}

		public static string StopFollowOnly()
		{
			try
			{
				object hero = FindHero(HeroId);
				if (hero == null)
					return Finish("Aelfeyja not found.");

				var notes = new List<string>();
				bool stopped = TryStopAIInfluenceAction(hero, notes);
				return Finish("STOP FOLLOW: StopAction=" + stopped + ". " + string.Join(" ", notes));
			}
			catch (Exception ex)
			{
				return Finish("STOP FOLLOW ERROR: " + Flatten(ex));
			}
		}

		public static string ConservativeFix()
		{
			try
			{
				object hero = FindHero(HeroId);
				if (hero == null)
					return Finish("Aelfeyja not found.");

				var notes = new List<string>();
				bool stopped = TryStopAIInfluenceAction(hero, notes);
				bool repaired = RepairPartyMovement(hero, notes);
				return Finish("CONSERVATIVE FIX: StopAction=" + stopped + ", PartyRepair=" + repaired + ". " + string.Join(" ", notes));
			}
			catch (Exception ex)
			{
				return Finish("FIX ERROR: " + Flatten(ex));
			}
		}

		public static string HardFix()
		{
			try
			{
				object hero = FindHero(HeroId);
				if (hero == null)
					return Finish("Aelfeyja not found. No changes made.");

				var notes = new List<string>();
				notes.Add("Target=" + HeroId + ".");

				bool stopped = TryStopAIInfluenceAction(hero, notes);
				object oldParty = GetProperty(hero, "PartyBelongedTo");
				if (oldParty != null)
					notes.Add("Existing party=" + SafeName(oldParty) + ".");
				else
					notes.Add("PartyBelongedTo already null.");

				bool fugitive = TryMakeHeroFugitive(hero, notes);
				object newParty = GetProperty(hero, "PartyBelongedTo");
				bool detached = newParty == null;

				if (!fugitive && oldParty != null)
				{
					notes.Add("MakeHeroFugitiveAction unavailable/failed; trying DestroyPartyAction fallback.");
					bool destroyed = TryDestroyParty(oldParty, notes);
					newParty = GetProperty(hero, "PartyBelongedTo");
					detached = destroyed || newParty == null;
				}

				return Finish("HARD FIX: StopAction=" + stopped
					+ ", MakeFugitive=" + fugitive
					+ ", PartyDetached=" + detached
					+ ". " + string.Join(" ", notes));
			}
			catch (Exception ex)
			{
				return Finish("HARD FIX ERROR: " + Flatten(ex));
			}
		}

		public static string DestroyPartyOnly()
		{
			try
			{
				object hero = FindHero(HeroId);
				if (hero == null)
					return Finish("Aelfeyja not found. No changes made.");

				object party = GetProperty(hero, "PartyBelongedTo");
				if (party == null)
					return Finish("Aelfeyja has no party; nothing to destroy.");

				var notes = new List<string>();
				TryStopAIInfluenceAction(hero, notes);
				bool destroyed = TryDestroyParty(party, notes);
				return Finish("DESTROY PARTY: Success=" + destroyed + ". " + string.Join(" ", notes));
			}
			catch (Exception ex)
			{
				return Finish("DESTROY PARTY ERROR: " + Flatten(ex));
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
			Type heroType = FindType("TaleWorlds.CampaignSystem.Hero");
			if (heroType != null)
			{
				foreach (string propertyName in new[] { "AllAliveHeroes", "AllHeroes", "All" })
				{
					PropertyInfo p = heroType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
					object heroes = p == null ? null : SafeGetValue(p, null);
					object found = FindHeroInEnumerable(heroes as IEnumerable, stringId);
					if (found != null)
						return found;
				}
			}

			Type campaignType = FindType("TaleWorlds.CampaignSystem.Campaign");
			object campaign = campaignType == null ? null : GetStaticMember(campaignType, "Current");
			if (campaign != null)
			{
				foreach (string propertyName in new[] { "AliveHeroes", "Heroes" })
				{
					object found = FindHeroInEnumerable(GetProperty(campaign, propertyName) as IEnumerable, stringId);
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
					notes.Add("AIActionManager is not loaded (AI Influence may be disabled). ");
					return false;
				}

				object manager = GetStaticMember(managerType, "Instance")
					?? GetStaticMember(managerType, "_instance")
					?? GetStaticMember(managerType, "instance");

				foreach (MethodInfo method in managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).Where(m => m.Name == "StopAction"))
				{
					ParameterInfo[] ps = method.GetParameters();
					object[] callArgs = new object[ps.Length];
					bool heroAssigned = false;
					bool actionAssigned = false;
					bool compatible = true;

					for (int i = 0; i < ps.Length; i++)
					{
						Type pt = ps[i].ParameterType;
						if (!heroAssigned && pt.IsInstanceOfType(hero))
						{
							callArgs[i] = hero;
							heroAssigned = true;
						}
						else if (!actionAssigned && pt == typeof(string))
						{
							callArgs[i] = ActionName;
							actionAssigned = true;
						}
						else if (pt == typeof(bool))
						{
							callArgs[i] = true;
						}
						else if (ps[i].HasDefaultValue)
						{
							callArgs[i] = ps[i].DefaultValue;
						}
						else if (!pt.IsValueType)
						{
							callArgs[i] = null;
						}
						else
						{
							compatible = false;
							break;
						}
					}

					if (!compatible || !heroAssigned || !actionAssigned || (!method.IsStatic && manager == null))
						continue;

					method.Invoke(method.IsStatic ? null : manager, callArgs);
					notes.Add("AI Influence StopAction(follow_player) invoked.");
					return true;
				}

				notes.Add("No compatible AI Influence StopAction overload found.");
				return false;
			}
			catch (Exception ex)
			{
				notes.Add("StopAction failed: " + Flatten(ex));
				return false;
			}
		}

		private static bool RepairPartyMovement(object hero, List<string> notes)
		{
			object party = GetProperty(hero, "PartyBelongedTo");
			if (party == null)
			{
				notes.Add("Aelfeyja has no party.");
				return true;
			}

			bool changed = false;
			object ai = GetProperty(party, "Ai");
			if (ai != null && InvokeNamed(ai, "SetDoNotMakeNewDecisions", new object[] { false }))
			{
				notes.Add("Normal party AI decisions re-enabled.");
				changed = true;
			}

			if (ai != null && InvokeNamed(ai, "SetMoveModeHold", new object[0]))
			{
				notes.Add("AI SetMoveModeHold invoked.");
				changed = true;
			}
			else if (InvokeNamed(party, "SetMoveModeHold", new object[0]))
			{
				notes.Add("Party SetMoveModeHold invoked.");
				changed = true;
			}

			return changed;
		}

		private static bool TryMakeHeroFugitive(object hero, List<string> notes)
		{
			try
			{
				Type actionType = FindType("TaleWorlds.CampaignSystem.Actions.MakeHeroFugitiveAction");
				if (actionType == null)
				{
					notes.Add("MakeHeroFugitiveAction type not found.");
					return false;
				}

				foreach (MethodInfo method in actionType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "Apply"))
				{
					ParameterInfo[] ps = method.GetParameters();
					object[] callArgs = new object[ps.Length];
					bool heroAssigned = false;
					bool compatible = true;

					for (int i = 0; i < ps.Length; i++)
					{
						Type pt = ps[i].ParameterType;
						if (!heroAssigned && pt.IsInstanceOfType(hero))
						{
							callArgs[i] = hero;
							heroAssigned = true;
						}
						else if (pt == typeof(bool))
						{
							callArgs[i] = false;
						}
						else if (ps[i].HasDefaultValue)
						{
							callArgs[i] = ps[i].DefaultValue;
						}
						else if (!pt.IsValueType)
						{
							callArgs[i] = null;
						}
						else
						{
							compatible = false;
							break;
						}
					}

					if (!compatible || !heroAssigned)
						continue;

					method.Invoke(null, callArgs);
					notes.Add("Bannerlord MakeHeroFugitiveAction.Apply invoked.");
					return true;
				}

				notes.Add("No compatible MakeHeroFugitiveAction.Apply overload found.");
				return false;
			}
			catch (Exception ex)
			{
				notes.Add("MakeHeroFugitiveAction failed: " + Flatten(ex));
				return false;
			}
		}

		private static bool TryDestroyParty(object party, List<string> notes)
		{
			try
			{
				if (party == null)
					return true;

				Type actionType = FindType("TaleWorlds.CampaignSystem.Actions.DestroyPartyAction");
				if (actionType == null)
				{
					notes.Add("DestroyPartyAction type not found.");
					return false;
				}

				foreach (MethodInfo method in actionType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name == "Apply"))
				{
					ParameterInfo[] ps = method.GetParameters();
					object[] callArgs = new object[ps.Length];
					bool partyAssigned = false;
					bool compatible = true;

					for (int i = 0; i < ps.Length; i++)
					{
						Type pt = ps[i].ParameterType;
						if (!partyAssigned && pt.IsInstanceOfType(party))
						{
							callArgs[i] = party;
							partyAssigned = true;
						}
						else if (pt == typeof(bool))
						{
							callArgs[i] = false;
						}
						else if (ps[i].HasDefaultValue)
						{
							callArgs[i] = ps[i].DefaultValue;
						}
						else if (!pt.IsValueType)
						{
							callArgs[i] = null;
						}
						else
						{
							compatible = false;
							break;
						}
					}

					if (!compatible || !partyAssigned)
						continue;

					method.Invoke(null, callArgs);
					notes.Add("Bannerlord DestroyPartyAction.Apply invoked for " + SafeName(party) + ".");
					return true;
				}

				notes.Add("No compatible DestroyPartyAction.Apply overload found.");
				return false;
			}
			catch (Exception ex)
			{
				notes.Add("DestroyPartyAction failed: " + Flatten(ex));
				return false;
			}
		}

		private static bool InvokeNamed(object target, string name, object[] args)
		{
			if (target == null)
				return false;

			foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Where(m => m.Name == name && m.GetParameters().Length == args.Length))
			{
				try
				{
					method.Invoke(target, args);
					return true;
				}
				catch { }
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
			return SafeGetValue(p, obj);
		}

		private static object SafeGetValue(PropertyInfo property, object target)
		{
			try { return property.GetValue(target, null); }
			catch { return null; }
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
			object name = GetProperty(obj, "Name");
			return name == null ? obj.GetType().Name : name.ToString();
		}

		private static string SafeValue(object value)
		{
			return value == null ? "null" : value.ToString();
		}

		private static string Finish(string message)
		{
			_lastResult = message;
			LogPublic(message);
			return message;
		}

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null)
				ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		public static void LogPublic(string message)
		{
			try
			{
				string baseDir = AppDomain.CurrentDomain.BaseDirectory;
				DirectoryInfo parent = Directory.GetParent(baseDir);
				string root = parent != null && parent.Parent != null ? parent.Parent.FullName : baseDir;
				string moduleDir = Path.Combine(root, "Modules", "AelfeyjaRescue");
				Directory.CreateDirectory(moduleDir);
				File.AppendAllText(Path.Combine(moduleDir, "rescue.log"), "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
			}
			catch { }
		}
	}
}
