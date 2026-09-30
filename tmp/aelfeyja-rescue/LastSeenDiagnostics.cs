using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;
using TaleWorlds.Library;

namespace AelfeyjaRescue
{
	public sealed class LastSeenDiagnosticsSubModule : MBSubModuleBase
	{
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			LastSeenDiagnostics.Initialize();
		}
	}

	public static class LastSeenDiagnosticCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("diag_last_seen", "rescue")]
		public static string DumpLastSeen(List<string> args)
		{
			if (args == null || args.Count == 0)
				return "Usage: rescue.diag_last_seen <Hero StringId>";
			return LastSeenDiagnostics.ManualDump(args[0]);
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_last_seen_status", "rescue")]
		public static string Status(List<string> args) => LastSeenDiagnostics.Status();
	}

	internal static class LastSeenDiagnostics
	{
		private const string ModVersion = "v1.7.2";
		private const string HarmonyId = "aik.bannerlord.character_crash_guard.last_seen.v172";
		private static readonly BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static readonly object Sync = new object();
		private static Harmony _harmony;
		private static bool _initialized;
		private static bool _patched;
		private static string _logPath;
		private static string _lastHeroId = "<none>";
		private static DateTime _lastLoggedUtc = DateTime.MinValue;

		internal static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;
			try
			{
				_harmony = new Harmony(HarmonyId);
				Type helperType = AccessTools.TypeByName("Helpers.HeroHelper");
				if (helperType == null)
				{
					Write("[LAST SEEN DIAGNOSTICS]\r\nPatch status: Helpers.HeroHelper type not found.\r\n");
					return;
				}

				MethodInfo target = helperType.GetMethod("GetLastSeenText", SF, null, new[] { typeof(Hero) }, null);
				if (target == null)
				{
					Write("[LAST SEEN DIAGNOSTICS]\r\nPatch status: HeroHelper.GetLastSeenText(Hero) not found.\r\n");
					return;
				}

				_harmony.Patch(
					target,
					prefix: new HarmonyMethod(typeof(LastSeenDiagnostics).GetMethod(nameof(Prefix), SF)),
					finalizer: new HarmonyMethod(typeof(LastSeenDiagnostics).GetMethod(nameof(Finalizer), SF)));
				_patched = true;
				Write("[LAST SEEN DIAGNOSTICS]\r\nVersion: " + ModVersion + "\r\nPatched: Helpers.HeroHelper.GetLastSeenText(Hero)\r\nMode: diagnostic only; original exceptions are preserved\r\n");
			}
			catch (Exception ex)
			{
				EmergencyWrite("LastSeenDiagnostics initialization failed: " + Flatten(ex));
			}
		}

		public static string Status()
		{
			return "LAST_SEEN_V172 initialized=" + _initialized + " patched=" + _patched + " log=" + ResolveLogPath();
		}

		public static void Prefix(object[] __args, MethodBase __originalMethod)
		{
			try
			{
				Hero hero = (__args != null && __args.Length > 0) ? __args[0] as Hero : null;
				if (hero == null) return;

				string id = SafeStringId(hero);
				DateTime now = DateTime.UtcNow;
				lock (Sync)
				{
					if (id == _lastHeroId && (now - _lastLoggedUtc).TotalMilliseconds < 750)
						return;
					_lastHeroId = id;
					_lastLoggedUtc = now;
				}

				Write(BuildReport(hero, "BEFORE GetLastSeenText", null));
			}
			catch (Exception ex)
			{
				EmergencyWrite("LastSeenDiagnostics prefix failed: " + Flatten(ex));
			}
		}

		public static Exception Finalizer(object[] __args, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception == null) return null;
			try
			{
				Hero hero = (__args != null && __args.Length > 0) ? __args[0] as Hero : null;
				Write(BuildReport(hero, "EXCEPTION in GetLastSeenText", __exception));
			}
			catch (Exception loggerEx)
			{
				EmergencyWrite("LastSeenDiagnostics finalizer failed: " + Flatten(loggerEx) + " | original=" + Flatten(__exception));
			}
			return __exception;
		}

		public static string ManualDump(string heroStringId)
		{
			try
			{
				CharacterObject co = MBObjectManager.Instance.GetObject<CharacterObject>(heroStringId);
				Hero hero = co?.HeroObject;
				if (hero == null)
					return "Could not resolve Hero from CharacterObject StringId '" + heroStringId + "'.";

				Write(BuildReport(hero, "MANUAL COMMAND", null));
				object last = ReadMember(hero, "LastKnownClosestSettlement").Value;
				return "LAST_SEEN_DUMP hero=" + Describe(hero) + " lastKnown=" + Describe(last) + " log=" + ResolveLogPath();
			}
			catch (Exception ex)
			{
				EmergencyWrite("Manual last-seen dump failed: " + Flatten(ex));
				return "diag_last_seen failed: " + ex.GetType().Name + ": " + ex.Message;
			}
		}

		private static string BuildReport(Hero hero, string phase, Exception ex)
		{
			StringBuilder sb = new StringBuilder(8192);
			sb.AppendLine();
			sb.AppendLine("============================================================");
			sb.AppendLine("CHARACTER CRASH GUARD - LAST SEEN FORENSICS");
			sb.AppendLine("============================================================");
			sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
			sb.AppendLine("Phase: " + phase);
			sb.AppendLine("Mod version: " + ModVersion);
			sb.AppendLine();
			sb.AppendLine("[HERO]");
			if (hero == null)
			{
				sb.AppendLine("Hero: <null>");
				if (ex != null) AppendException(sb, ex);
				return sb.ToString();
			}

			AppendProbe(sb, "Hero", hero, null);
			AppendProbe(sb, "Hero.Name", hero, "Name");
			AppendProbe(sb, "Hero.StringId", hero, "StringId");
			AppendProbe(sb, "Hero.HeroState", hero, "HeroState");
			AppendProbe(sb, "Hero.IsPrisoner", hero, "IsPrisoner");
			AppendProbe(sb, "Hero.CurrentSettlement", hero, "CurrentSettlement");
			AppendProbe(sb, "Hero.StayingInSettlement", hero, "StayingInSettlement");
			AppendProbe(sb, "Hero.HomeSettlement", hero, "HomeSettlement");
			AppendProbe(sb, "Hero.PartyBelongedTo", hero, "PartyBelongedTo");
			AppendProbe(sb, "Hero.PartyBelongedToAsPrisoner", hero, "PartyBelongedToAsPrisoner");
			AppendProbe(sb, "Hero.LastKnownClosestSettlement", hero, "LastKnownClosestSettlement");

			ReadResult lastResult = ReadMember(hero, "LastKnownClosestSettlement");
			object last = lastResult.Value;
			sb.AppendLine();
			sb.AppendLine("[LAST KNOWN CLOSEST SETTLEMENT]");
			if (lastResult.Error != null)
			{
				sb.AppendLine("Reading Hero.LastKnownClosestSettlement threw: " + lastResult.Error.GetType().FullName + ": " + lastResult.Error.Message);
			}
			else if (last == null)
			{
				sb.AppendLine("Value: <null> (vanilla GetLastSeenText should take the safe 'never seen' branch)");
			}
			else
			{
				AppendProbe(sb, "Settlement", last, null);
				foreach (string p in new[] { "StringId", "Name", "EncyclopediaLink", "EncyclopediaLinkWithName", "IsTown", "IsCastle", "IsVillage", "IsHideout", "IsFortification", "OwnerClan", "MapFaction", "Culture", "SettlementComponent", "Town", "Village", "Party" })
					AppendProbe(sb, "Settlement." + p, last, p);
				AppendCanonicalSettlementCheck(sb, last);
			}

			object prisonerPartyBase = ReadMember(hero, "PartyBelongedToAsPrisoner").Value;
			sb.AppendLine();
			sb.AppendLine("[PRISONER PARTY]");
			if (prisonerPartyBase == null)
			{
				sb.AppendLine("PartyBelongedToAsPrisoner: <null>");
			}
			else
			{
				AppendProbe(sb, "PartyBase", prisonerPartyBase, null);
				foreach (string p in new[] { "Name", "IsMobile", "IsSettlement", "LeaderHero", "Owner", "MapFaction", "Settlement", "MobileParty" })
					AppendProbe(sb, "PartyBase." + p, prisonerPartyBase, p);

				object mobile = ReadMember(prisonerPartyBase, "MobileParty").Value;
				if (mobile != null)
				{
					sb.AppendLine("-- MobileParty details --");
					foreach (string p in new[] { "Name", "StringId", "LeaderHero", "ActualClan", "MapFaction", "CurrentSettlement", "HomeSettlement", "IsActive", "PartyComponent", "Position" })
						AppendProbe(sb, "MobileParty." + p, mobile, p);
					object pc = ReadMember(mobile, "PartyComponent").Value;
					sb.AppendLine("MobileParty.PartyComponent.Type: " + (pc == null ? "<null>" : pc.GetType().AssemblyQualifiedName));
				}
			}

			sb.AppendLine();
			sb.AppendLine("[INFERENCE AIDS]");
			if (last == null && lastResult.Error == null)
				sb.AppendLine("- LastKnownClosestSettlement is null. If vanilla still crashes, the fault is not a malformed settlement reference and likely lies in another expression inside GetLastSeenText.");
			else if (last != null)
				sb.AppendLine("- LastKnownClosestSettlement is non-null. Inspect the individual getter results above, especially EncyclopediaLinkWithName and canonical-registration status.");
			sb.AppendLine("- This diagnostic does not mutate Hero, Settlement, PartyBase, or MobileParty state.");

			if (ex != null) AppendException(sb, ex);
			sb.AppendLine("============================================================");
			return sb.ToString();
		}

		private static void AppendCanonicalSettlementCheck(StringBuilder sb, object settlement)
		{
			try
			{
				string id = SafeStringId(settlement);
				sb.AppendLine("Settlement canonical check StringId: " + id);
				if (string.IsNullOrEmpty(id) || id.StartsWith("<"))
				{
					sb.AppendLine("Settlement canonical lookup: SKIPPED (missing/unreadable StringId)");
					return;
				}

				Type settlementType = settlement.GetType();
				object canonical = null;
				try
				{
					MethodInfo getObj = typeof(MBObjectManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
						.FirstOrDefault(m => m.Name == "GetObject" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
					if (getObj != null)
					{
						MethodInfo gm = getObj.MakeGenericMethod(settlementType);
						canonical = gm.Invoke(MBObjectManager.Instance, new object[] { id });
					}
				}
				catch (Exception lookupEx)
				{
					sb.AppendLine("Settlement canonical lookup error: " + Flatten(lookupEx));
				}

				sb.AppendLine("Settlement canonical object: " + Describe(canonical));
				sb.AppendLine("Settlement ReferenceEquals(saved, canonical): " + (canonical != null && ReferenceEquals(settlement, canonical)));
			}
			catch (Exception ex)
			{
				sb.AppendLine("Settlement canonical check failed: " + Flatten(ex));
			}
		}

		private static void AppendProbe(StringBuilder sb, string label, object obj, string member)
		{
			if (member == null)
			{
				sb.AppendLine(label + ": " + Describe(obj));
				return;
			}
			ReadResult r = ReadMember(obj, member);
			if (r.Error != null)
				sb.AppendLine(label + ": <ERROR reading property: " + r.Error.GetType().Name + ": " + r.Error.Message + ">");
			else
				sb.AppendLine(label + ": " + Describe(r.Value));
		}

		private static ReadResult ReadMember(object obj, string member)
		{
			if (obj == null) return new ReadResult { Value = null };
			try
			{
				Type t = obj.GetType();
				PropertyInfo p = t.GetProperty(member, IF);
				if (p != null)
					return new ReadResult { Value = p.GetValue(obj, null) };
				FieldInfo f = t.GetField(member, IF);
				if (f != null)
					return new ReadResult { Value = f.GetValue(obj) };
				return new ReadResult { Error = new MissingMemberException(t.FullName, member) };
			}
			catch (TargetInvocationException tie)
			{
				return new ReadResult { Error = tie.InnerException ?? tie };
			}
			catch (Exception ex)
			{
				return new ReadResult { Error = ex };
			}
		}

		private static string Describe(object obj)
		{
			if (obj == null) return "<null>";
			try
			{
				if (obj is TaleWorlds.Localization.TextObject tx) return tx.ToString();
				if (obj is string s) return s;
				if (obj is bool || obj is int || obj is long || obj is float || obj is double || obj is decimal || obj.GetType().IsEnum) return Convert.ToString(obj);
				string name = SafeName(obj);
				string id = SafeStringId(obj);
				return obj.GetType().Name + "{Name=" + name + ", StringId=" + id + "}";
			}
			catch (Exception ex)
			{
				return "<ERROR describing " + obj.GetType().FullName + ": " + ex.GetType().Name + ">";
			}
		}

		private static string SafeName(object obj)
		{
			ReadResult r = ReadMember(obj, "Name");
			if (r.Error != null) return "<ERROR:" + r.Error.GetType().Name + ">";
			if (r.Value == null) return "<null>";
			try { return r.Value.ToString(); } catch { return "<ERROR:ToString>"; }
		}

		private static string SafeStringId(object obj)
		{
			ReadResult r = ReadMember(obj, "StringId");
			if (r.Error != null) return "<ERROR:" + r.Error.GetType().Name + ">";
			return r.Value == null ? "<null>" : Convert.ToString(r.Value);
		}

		private static void AppendException(StringBuilder sb, Exception ex)
		{
			sb.AppendLine();
			sb.AppendLine("[EXCEPTION]");
			int depth = 0;
			for (Exception cur = ex; cur != null && depth < 8; cur = cur.InnerException, depth++)
			{
				sb.AppendLine("Depth " + depth + ": " + cur.GetType().FullName);
				sb.AppendLine("Message: " + cur.Message);
				sb.AppendLine("StackTrace:");
				sb.AppendLine(cur.StackTrace ?? "<null>");
			}
		}

		private static string ResolveLogPath()
		{
			if (!string.IsNullOrEmpty(_logPath)) return _logPath;
			try
			{
				string bin = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				DirectoryInfo d = new DirectoryInfo(bin);
				for (int i = 0; i < 2 && d != null; i++) d = d.Parent;
				string moduleRoot = d?.FullName ?? bin;
				string logs = Path.Combine(moduleRoot, "Logs");
				Directory.CreateDirectory(logs);
				FileInfo latest = new DirectoryInfo(logs).GetFiles("CharacterCrashGuard_*.log")
					.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
				_logPath = latest != null ? latest.FullName : Path.Combine(logs, "CharacterCrashGuard_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log");
				return _logPath;
			}
			catch
			{
				_logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CharacterCrashGuard_LastSeen.log");
				return _logPath;
			}
		}

		private static void Write(string text)
		{
			try
			{
				lock (Sync)
				{
					File.AppendAllText(ResolveLogPath(), text + (text.EndsWith("\n") ? "" : Environment.NewLine), Encoding.UTF8);
				}
			}
			catch (Exception ex) { EmergencyWrite("Write failed: " + Flatten(ex)); }
		}

		private static void EmergencyWrite(string text)
		{
			try
			{
				string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CharacterCrashGuard_LastSeen_Emergency.log");
				File.AppendAllText(p, DateTime.Now.ToString("o") + " " + text + Environment.NewLine);
			}
			catch { }
		}

		private static string Flatten(Exception ex)
		{
			if (ex == null) return "<null>";
			StringBuilder sb = new StringBuilder();
			for (Exception cur = ex; cur != null; cur = cur.InnerException)
			{
				if (sb.Length > 0) sb.Append(" -> ");
				sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
			}
			return sb.ToString();
		}

		private sealed class ReadResult
		{
			public object Value;
			public Exception Error;
		}
	}
}
