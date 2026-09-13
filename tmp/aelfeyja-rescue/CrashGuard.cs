using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	public sealed class GuardSubModule : MBSubModuleBase
	{
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			CrashGuard.Initialize();
			CharacterScanner.Log("Character Crash Guard v1.5 loaded. Scanner/repair commands remain available.");
		}
	}

	public static class GuardCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("guard_status", "rescue")]
		public static string GuardStatus(List<string> args) => CrashGuard.Status();

		[CommandLineFunctionality.CommandLineArgumentFunction("guard_on", "rescue")]
		public static string GuardOn(List<string> args) => CrashGuard.SetEnabled(true);

		[CommandLineFunctionality.CommandLineArgumentFunction("guard_off", "rescue")]
		public static string GuardOff(List<string> args) => CrashGuard.SetEnabled(false);

		[CommandLineFunctionality.CommandLineArgumentFunction("guard_log_path", "rescue")]
		public static string GuardLogPath(List<string> args) => CrashGuard.GetLogPath();

		[CommandLineFunctionality.CommandLineArgumentFunction("guard_clear_log", "rescue")]
		public static string GuardClearLog(List<string> args) => CrashGuard.ClearLog();
	}

	internal static class CrashGuard
	{
		private static readonly BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private const string HarmonyId = "aik.bannerlord.character_crash_guard.v15";
		private static readonly object Sync = new object();
		private static readonly Dictionary<string, DateTime> LastMessageByCharacter = new Dictionary<string, DateTime>(StringComparer.Ordinal);
		private static Harmony _harmony;
		private static bool _initialized;
		private static bool _enabled = true;
		private static int _patchedMethods;
		private static int _blockedFatalInserts;
		private static int _fatalNoCanonical;
		private static string _lastIncident = "none";

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;
			try
			{
				_harmony = new Harmony(HarmonyId);
				Type rosterType = FindType("TaleWorlds.CampaignSystem.Roster.TroopRoster");
				if (rosterType == null)
				{
					GuardLog("INIT_FAILED TroopRoster type not found.");
					return;
				}

				MethodInfo prefix = typeof(CrashGuard).GetMethod(nameof(RosterCharacterInsertPrefix), StaticFlags);
				if (prefix == null)
				{
					GuardLog("INIT_FAILED prefix method not found.");
					return;
				}
				HarmonyMethod harmonyPrefix = new HarmonyMethod(prefix);

				foreach (MethodInfo method in rosterType.GetMethods(InstanceFlags))
				{
					if (method.Name != "AddToCounts" && method.Name != "AddNewElement") continue;
					ParameterInfo[] p = method.GetParameters();
					if (p.Length == 0 || p[0].ParameterType.FullName != "TaleWorlds.CampaignSystem.CharacterObject") continue;
					_harmony.Patch(method, prefix: harmonyPrefix);
					_patchedMethods++;
					GuardLog("PATCHED " + FormatMethod(method));
				}

				GuardLog("Character Crash Guard v1.5 initialized. enabled=" + _enabled + " patchedMethods=" + _patchedMethods + ". Fatal malformed regular troop inserts will be replaced with the healthy canonical CharacterObject when one exists, and the full managed call stack will be logged.");
			}
			catch (Exception ex)
			{
				GuardLog("INIT_EXCEPTION " + Flatten(ex));
			}
		}

		public static void RosterCharacterInsertPrefix(object __instance, object[] __args, MethodBase __originalMethod)
		{
			if (!_enabled || __args == null || __args.Length == 0) return;
			object incoming = __args[0];
			if (incoming == null) return;

			try
			{
				string reason;
				if (!IsFatalRegularCharacter(incoming, out reason)) return;

				string charId = GetStringId(incoming);
				string partyId = GetRosterOwnerId(__instance);
				string rosterKind = GetRosterKind(__instance);
				StackTrace trace = new StackTrace(1, false);
				string likelyCaller = FindLikelyCaller(trace);
				string stack = FormatStack(trace);
				object canonical = FindHealthyCanonicalCharacter(charId, incoming);

				if (canonical != null)
				{
					__args[0] = canonical;
					_blockedFatalInserts++;
					_lastIncident = charId + " <- " + likelyCaller + " @ " + partyId;
					GuardLog("===== BLOCKED MALFORMED TROOP INSERT =====\n" +
						"reason=" + reason + "\n" +
						"character=" + charId + "\n" +
						"incoming_ref=" + RuntimeHelpers.GetHashCode(incoming) + "\n" +
						"canonical_ref=" + RuntimeHelpers.GetHashCode(canonical) + "\n" +
						"party=" + partyId + "\n" +
						"roster=" + rosterKind + "\n" +
						"intercepted_method=" + FormatMethod(__originalMethod) + "\n" +
						"likely_caller=" + likelyCaller + "\n" +
						"stack:\n" + stack +
						"===== END INCIDENT =====");
					ShowGuardMessage(charId, likelyCaller, true);
				}
				else
				{
					_fatalNoCanonical++;
					_lastIncident = charId + " (NO CANONICAL) <- " + likelyCaller + " @ " + partyId;
					GuardLog("===== MALFORMED TROOP INSERT - NO CANONICAL DONOR =====\n" +
						"reason=" + reason + "\n" +
						"character=" + charId + "\n" +
						"incoming_ref=" + RuntimeHelpers.GetHashCode(incoming) + "\n" +
						"party=" + partyId + "\n" +
						"roster=" + rosterKind + "\n" +
						"intercepted_method=" + FormatMethod(__originalMethod) + "\n" +
						"likely_caller=" + likelyCaller + "\n" +
						"stack:\n" + stack +
						"===== END INCIDENT =====");
					ShowGuardMessage(charId, likelyCaller, false);
				}
			}
			catch (Exception ex)
			{
				GuardLog("PREFIX_EXCEPTION method=" + FormatMethod(__originalMethod) + " error=" + Flatten(ex));
			}
		}

		public static string Status()
		{
			return "GUARD_V15 initialized=" + _initialized +
				", enabled=" + _enabled +
				", patchedMethods=" + _patchedMethods +
				", blockedFatalInserts=" + _blockedFatalInserts +
				", fatalNoCanonical=" + _fatalNoCanonical +
				", last=" + _lastIncident +
				", log=" + GetLogPath();
		}

		public static string SetEnabled(bool enabled)
		{
			_enabled = enabled;
			GuardLog("GUARD_ENABLED_CHANGED enabled=" + enabled);
			return Status();
		}

		public static string ClearLog()
		{
			try
			{
				string path = GetLogPath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.WriteAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Character Crash Guard v1.5 log cleared by user.\r\n");
				return "CLEARED: " + path;
			}
			catch (Exception ex)
			{
				return "CLEAR_LOG_ERROR: " + Flatten(ex);
			}
		}

		public static string GetLogPath()
		{
			string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			return Path.Combine(docs, "Mount and Blade II Bannerlord", "CharacterCrashGuard.log");
		}

		private static bool IsFatalRegularCharacter(object character, out string reason)
		{
			reason = null;
			if (character == null) return false;
			object isHero = GetProperty(character, "IsHero");
			object heroObject = SafeFieldGet(FindField(character.GetType(), "_heroObject"), character);
			if ((isHero is bool && (bool)isHero) || heroObject != null) return false;

			FieldInfo field = FindField(character.GetType(), "DefaultCharacterSkills");
			if (field == null) return false;
			object defaultSkills = SafeFieldGet(field, character);
			if (defaultSkills == null)
			{
				reason = "REGULAR_DEFAULT_SKILLS_NULL";
				return true;
			}
			object skills = GetMember(defaultSkills, "Skills");
			if (skills == null)
			{
				reason = "REGULAR_DEFAULT_SKILLS_INNER_NULL";
				return true;
			}
			return false;
		}

		private static object FindHealthyCanonicalCharacter(string id, object badReference)
		{
			if (string.IsNullOrEmpty(id) || id == "<no-id>" || id == "<null>") return null;
			Type characterType = FindType("TaleWorlds.CampaignSystem.CharacterObject");
			object all = GetStaticMember(characterType, "All");
			IEnumerable enumerable = all as IEnumerable;
			if (enumerable == null) return null;
			foreach (object candidate in enumerable)
			{
				if (candidate == null || object.ReferenceEquals(candidate, badReference)) continue;
				if (!string.Equals(GetStringId(candidate), id, StringComparison.Ordinal)) continue;
				string reason;
				if (IsFatalRegularCharacter(candidate, out reason)) continue;
				object isHero = GetProperty(candidate, "IsHero");
				if (isHero is bool && (bool)isHero) continue;
				return candidate;
			}
			return null;
		}

		private static string GetRosterOwnerId(object roster)
		{
			object owner = GetProperty(roster, "OwnerParty") ?? SafeFieldGet(FindField(roster == null ? null : roster.GetType(), "OwnerParty"), roster);
			if (owner == null) return "<no-owner>";
			object mobile = GetProperty(owner, "MobileParty");
			if (mobile != null)
			{
				string mobileId = GetStringId(mobile);
				if (!string.IsNullOrEmpty(mobileId) && mobileId != "<no-id>") return mobileId;
			}
			return GetStringId(owner);
		}

		private static string GetRosterKind(object roster)
		{
			try
			{
				object owner = GetProperty(roster, "OwnerParty");
				if (owner == null) return "unknown";
				object member = GetProperty(owner, "MemberRoster");
				if (object.ReferenceEquals(member, roster)) return "MemberRoster";
				object prison = GetProperty(owner, "PrisonRoster");
				if (object.ReferenceEquals(prison, roster)) return "PrisonRoster";
			}
			catch { }
			return "unknown";
		}

		private static string FindLikelyCaller(StackTrace trace)
		{
			StackFrame[] frames = trace.GetFrames();
			if (frames == null) return "unknown";
			foreach (StackFrame frame in frames)
			{
				MethodBase m = frame.GetMethod();
				if (m == null || m.DeclaringType == null) continue;
				string assembly = m.DeclaringType.Assembly.GetName().Name ?? "";
				if (IsFrameworkOrOfficialAssembly(assembly)) continue;
				return assembly + " :: " + m.DeclaringType.FullName + "." + m.Name;
			}
			return "vanilla_or_unknown";
		}

		private static bool IsFrameworkOrOfficialAssembly(string assembly)
		{
			if (string.IsNullOrEmpty(assembly)) return true;
			if (assembly == "AelfeyjaRescue" || assembly == "0Harmony" || assembly == "HarmonySharedState") return true;
			if (assembly == "mscorlib" || assembly == "netstandard") return true;
			if (assembly.StartsWith("System", StringComparison.Ordinal) || assembly.StartsWith("Microsoft", StringComparison.Ordinal)) return true;
			if (assembly.StartsWith("TaleWorlds", StringComparison.Ordinal)) return true;
			if (assembly.StartsWith("MonoMod", StringComparison.Ordinal)) return true;
			if (assembly.StartsWith("Bannerlord.Harmony", StringComparison.Ordinal) || assembly.StartsWith("Bannerlord.ButterLib", StringComparison.Ordinal)) return true;
			if (assembly == "SandBox" || assembly.StartsWith("SandBox.", StringComparison.Ordinal)) return true;
			if (assembly == "StoryMode" || assembly.StartsWith("StoryMode.", StringComparison.Ordinal)) return true;
			if (assembly == "NavalDLC" || assembly.StartsWith("NavalDLC.", StringComparison.Ordinal)) return true;
			if (assembly == "BirthAndDeath" || assembly == "CustomBattle") return true;
			return false;
		}

		private static string FormatStack(StackTrace trace)
		{
			StackFrame[] frames = trace.GetFrames();
			if (frames == null || frames.Length == 0) return "  <no managed frames>\n";
			List<string> lines = new List<string>();
			int limit = Math.Min(frames.Length, 50);
			for (int i = 0; i < limit; i++)
			{
				MethodBase m = frames[i].GetMethod();
				if (m == null) continue;
				string assembly = m.DeclaringType == null ? "<unknown-assembly>" : (m.DeclaringType.Assembly.GetName().Name ?? "<unknown-assembly>");
				string type = m.DeclaringType == null ? "<global>" : m.DeclaringType.FullName;
				lines.Add("  #" + i + " [" + assembly + "] " + type + "." + m.Name);
			}
			return string.Join("\n", lines.ToArray()) + "\n";
		}

		private static string FormatMethod(MethodBase method)
		{
			if (method == null) return "<null-method>";
			string type = method.DeclaringType == null ? "<global>" : method.DeclaringType.FullName;
			return type + "." + method.Name;
		}

		private static void ShowGuardMessage(string charId, string caller, bool blocked)
		{
			try
			{
				lock (Sync)
				{
					DateTime last;
					if (LastMessageByCharacter.TryGetValue(charId, out last) && (DateTime.Now - last).TotalSeconds < 5.0) return;
					LastMessageByCharacter[charId] = DateTime.Now;
				}
				string shortCaller = caller;
				int sep = shortCaller.IndexOf(" :: ", StringComparison.Ordinal);
				if (sep > 0) shortCaller = shortCaller.Substring(0, sep);
				string text = blocked
					? "[Character Guard] Blocked malformed troop '" + charId + "'. Caller: " + shortCaller
					: "[Character Guard] MALFORMED troop detected with no canonical donor: '" + charId + "'. Caller: " + shortCaller;
				InformationManager.DisplayMessage(new InformationMessage(text));
			}
			catch { }
		}

		private static string GetStringId(object obj)
		{
			if (obj == null) return "<null>";
			object id = GetMember(obj, "StringId");
			return id == null ? "<no-id>" : id.ToString();
		}

		private static object GetMember(object obj, string name)
		{
			if (obj == null) return null;
			object p = GetProperty(obj, name);
			if (p != null) return p;
			return SafeFieldGet(FindField(obj.GetType(), name), obj);
		}

		private static object GetProperty(object obj, string name)
		{
			if (obj == null) return null;
			try
			{
				PropertyInfo p = obj.GetType().GetProperty(name, InstanceFlags);
				return p == null ? null : p.GetValue(obj, null);
			}
			catch { return null; }
		}

		private static object GetStaticMember(Type type, string name)
		{
			if (type == null) return null;
			try
			{
				PropertyInfo p = type.GetProperty(name, StaticFlags);
				if (p != null) return p.GetValue(null, null);
			}
			catch { }
			try
			{
				FieldInfo f = type.GetField(name, StaticFlags);
				if (f != null) return f.GetValue(null);
			}
			catch { }
			return null;
		}

		private static FieldInfo FindField(Type type, string name)
		{
			for (Type t = type; t != null; t = t.BaseType)
			{
				FieldInfo f = t.GetField(name, InstanceFlags);
				if (f != null) return f;
			}
			return null;
		}

		private static object SafeFieldGet(FieldInfo field, object obj)
		{
			if (field == null || obj == null) return null;
			try { return field.GetValue(obj); } catch { return null; }
		}

		private static Type FindType(string fullName)
		{
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type t = a.GetType(fullName, false);
					if (t != null) return t;
				}
				catch { }
			}
			return null;
		}

		private static string Flatten(Exception ex)
		{
			while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
			return ex.GetType().Name + ": " + ex.Message;
		}

		private static void GuardLog(string message)
		{
			try
			{
				string path = GetLogPath();
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				File.AppendAllText(path, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message + Environment.NewLine);
			}
			catch { }
		}
	}
}
