using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encyclopedia;
using TaleWorlds.CampaignSystem.ViewModelCollection;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	/// <summary>
	/// Surgical encyclopedia diagnostics for Bannerlord 1.5.3.
	/// Intentionally patches only click/navigation methods. It does NOT patch
	/// HeroVM/page constructors or Refresh methods, so it stays dormant during
	/// campaign/save initialization.
	/// </summary>
	public sealed class CampaignDiagnosticsSubModule : MBSubModuleBase
	{
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			CampaignDiagnostics.Initialize();
		}
	}

	public static class CampaignDiagnosticCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("diag_status", "rescue")]
		public static string Status(List<string> args) => CampaignDiagnostics.Status();

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_log_path", "rescue")]
		public static string LogPath(List<string> args) => CampaignDiagnostics.LogPath;

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_breadcrumbs", "rescue")]
		public static string Breadcrumbs(List<string> args) => CampaignDiagnostics.DumpBreadcrumbsToLog();

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_dump_hero", "rescue")]
		public static string DumpHero(List<string> args)
		{
			if (args == null || args.Count == 0) return "Usage: rescue.diag_dump_hero <Hero StringId>";
			return CampaignDiagnostics.ManualDumpHero(args[0]);
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_dump_clan", "rescue")]
		public static string DumpClan(List<string> args)
		{
			if (args == null || args.Count == 0) return "Usage: rescue.diag_dump_clan <Clan StringId>";
			return CampaignDiagnostics.ManualDumpClan(args[0]);
		}
	}

	internal static class CampaignDiagnostics
	{
		private const string ModVersion = "v1.7.1";
		private const string TargetGameVersion = "v1.5.3.122374-beta";
		private const string HarmonyId = "aik.bannerlord.character_crash_guard.campaign_diagnostics.v171";
		private const int MaxBreadcrumbs = 100;
		private static readonly BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static readonly object Sync = new object();
		private static readonly Queue<string> Breadcrumbs = new Queue<string>();
		private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
		private static Harmony _harmony;
		private static bool _initialized;
		private static int _patchedCount;
		private static string _logPath;
		private static string _currentPage = "<unknown>";
		private static string _lastLink = "<none>";
		private static string _lastContext = "<none>";
		private static object _lastHero;
		private static object _lastClan;
		private static object _lastKingdom;
		private static object _lastSettlement;
		private static object _lastParty;
		private static int _lastExceptionRef;
		private static DateTime _lastExceptionAtUtc = DateTime.MinValue;

		public static string LogPath
		{
			get { EnsureLogPath(); return _logPath; }
		}

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;
			try
			{
				EnsureLogPath();
				WriteSessionHeader();
				_harmony = new Harmony(HarmonyId);
				PatchOnlySafeTargets();
				AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
				AddBreadcrumb("Character Crash Guard surgical encyclopedia diagnostics initialized " + ModVersion);
			}
			catch (Exception ex)
			{
				EmergencyWrite("DIAGNOSTIC INITIALIZATION FAILED: " + Flatten(ex));
			}
		}

		private static void PatchOnlySafeTargets()
		{
			Patch(typeof(HeroVM).GetMethod("ExecuteLink", IF, null, Type.EmptyTypes, null), nameof(HeroExecutePrefix), null, nameof(HeroExecuteFinalizer));
			Patch(typeof(EncyclopediaManager).GetMethod("GoToLink", IF, null, new[] { typeof(string) }, null), nameof(GoToLinkPrefix), nameof(GoToLinkPostfix), nameof(GoToLinkFinalizer));
			Patch(typeof(EncyclopediaManager).GetMethod("GoToLink", IF, null, new[] { typeof(string), typeof(string) }, null), nameof(GoToLinkPrefix), nameof(GoToLinkPostfix), nameof(GoToLinkFinalizer));
			Append("[PATCH STATUS]\r\nMode: SURGICAL - encyclopedia clicks/navigation only\r\nPatched methods: " + _patchedCount + "\r\nNo HeroVM/page constructors or Refresh methods are patched.\r\n");
		}

		private static void Patch(MethodBase method, string prefixName, string postfixName, string finalizerName)
		{
			if (method == null || Patched.Contains(method)) return;
			try
			{
				HarmonyMethod pre = prefixName == null ? null : new HarmonyMethod(typeof(CampaignDiagnostics).GetMethod(prefixName, SF));
				HarmonyMethod post = postfixName == null ? null : new HarmonyMethod(typeof(CampaignDiagnostics).GetMethod(postfixName, SF));
				HarmonyMethod fin = finalizerName == null ? null : new HarmonyMethod(typeof(CampaignDiagnostics).GetMethod(finalizerName, SF));
				_harmony.Patch(method, prefix: pre, postfix: post, finalizer: fin);
				Patched.Add(method);
				_patchedCount++;
				WriteLine("PATCHED " + FormatMethod(method));
			}
			catch (Exception ex)
			{
				WriteLine("PATCH_FAILED " + FormatMethod(method) + " :: " + Flatten(ex));
			}
		}

		public static void HeroExecutePrefix(HeroVM __instance, MethodBase __originalMethod)
		{
			try
			{
				object hero = null;
				try { hero = __instance == null ? null : __instance.Hero; }
				catch { }
				_lastHero = hero;
				object clan = ReadValue(hero, "Clan").Value;
				if (clan != null) _lastClan = clan;
				CaptureRelatedContext(hero);
				string link = SafeText(ReadValue(hero, "EncyclopediaLink"));
				_lastLink = link;
				_lastContext = "HeroVM.ExecuteLink -> " + SafeDescribe(hero);
				AddBreadcrumb("Hero link clicked: " + SafeDescribe(hero) + " source=" + _currentPage + " link=" + link);

				StringBuilder sb = Section("ENCYCLOPEDIA EVENT");
				sb.AppendLine("Event: HeroVM.ExecuteLink");
				sb.AppendLine("Source page/link: " + _currentPage);
				sb.AppendLine("Target type: Hero");
				sb.AppendLine("Target: " + SafeDescribe(hero));
				sb.AppendLine("Target link: " + link);
				sb.AppendLine("Original method: " + FormatMethod(__originalMethod));
				sb.Append(DumpHero(hero));
				if (clan != null) sb.Append(DumpClan(clan));
				sb.Append(DumpValidation(hero));
				Append(sb.ToString());
			}
			catch (Exception ex)
			{
				WriteLine("HeroExecutePrefix diagnostic failure: " + Flatten(ex));
			}
		}

		public static Exception HeroExecuteFinalizer(HeroVM __instance, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception != null) ReportException(__exception, __originalMethod, __instance, "HeroVM.ExecuteLink");
			return __exception;
		}

		public static void GoToLinkPrefix(object[] __args, MethodBase __originalMethod)
		{
			try
			{
				string link = ArgsToLink(__args);
				_lastLink = link;
				_lastContext = "EncyclopediaManager.GoToLink(" + link + ")";
				AddBreadcrumb("GoToLink requested: " + link + " from=" + _currentPage);
			}
			catch { }
		}

		public static void GoToLinkPostfix(object[] __args, MethodBase __originalMethod)
		{
			try
			{
				string link = ArgsToLink(__args);
				_currentPage = link;
				AddBreadcrumb("GoToLink completed: " + link);
			}
			catch { }
		}

		public static Exception GoToLinkFinalizer(object[] __args, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception != null)
			{
				_lastLink = ArgsToLink(__args);
				ReportException(__exception, __originalMethod, null, "EncyclopediaManager.GoToLink");
			}
			return __exception;
		}

		private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			try
			{
				Exception ex = e == null ? null : e.ExceptionObject as Exception;
				if (ex != null) ReportException(ex, null, null, "AppDomain.UnhandledException terminating=" + e.IsTerminating);
				else WriteLine("UNHANDLED NON-EXCEPTION OBJECT: " + (e == null || e.ExceptionObject == null ? "<null>" : e.ExceptionObject.ToString()));
			}
			catch { }
		}

		private static void ReportException(Exception ex, MethodBase method, object instance, string context)
		{
			if (ex == null) return;
			try
			{
				int er = RuntimeHelpers.GetHashCode(ex);
				DateTime now = DateTime.UtcNow;
				lock (Sync)
				{
					if (er == _lastExceptionRef && (now - _lastExceptionAtUtc).TotalSeconds < 2.0) return;
					_lastExceptionRef = er;
					_lastExceptionAtUtc = now;
				}
				AddBreadcrumb("EXCEPTION: " + ex.GetType().FullName + " in " + FormatMethod(method));
				StringBuilder sb = new StringBuilder(20000);
				sb.AppendLine("============================================================");
				sb.AppendLine("CHARACTER CRASH GUARD - DIAGNOSTIC REPORT");
				sb.AppendLine("============================================================");
				sb.AppendLine();
				sb.AppendLine("[SESSION]");
				sb.AppendLine("Timestamp: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
				sb.AppendLine("Mod version: " + ModVersion);
				sb.AppendLine("Build target: Bannerlord " + TargetGameVersion);
				sb.AppendLine("Runtime game version: " + RuntimeVersion());
				sb.AppendLine("Thread: id=" + Thread.CurrentThread.ManagedThreadId + " name=" + (Thread.CurrentThread.Name ?? "<unnamed>"));
				sb.AppendLine("Harmony/original method: " + FormatMethod(method));
				sb.AppendLine("Context: " + context + " | " + _lastContext);
				sb.AppendLine("Current encyclopedia page/link: " + _currentPage);
				sb.AppendLine("Last link: " + _lastLink);
				sb.AppendLine("Last hero: " + SafeDescribe(_lastHero));
				sb.AppendLine("Last clan: " + SafeDescribe(_lastClan));
				sb.AppendLine("Last kingdom: " + SafeDescribe(_lastKingdom));
				sb.AppendLine("Last settlement: " + SafeDescribe(_lastSettlement));
				sb.AppendLine("Last party: " + SafeDescribe(_lastParty));
				sb.AppendLine();
				sb.AppendLine("[LOADED ASSEMBLIES]");
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
				{
					try { sb.AppendLine("  - " + a.GetName().Name + " " + a.GetName().Version); } catch { }
				}
				sb.AppendLine();
				if (_lastHero != null) sb.Append(DumpHero(_lastHero));
				if (_lastClan != null) sb.Append(DumpClan(_lastClan));
				if (_lastKingdom != null) sb.Append(DumpKingdom(_lastKingdom));
				if (_lastSettlement != null) sb.Append(DumpSettlement(_lastSettlement));
				if (_lastParty != null) sb.Append(DumpParty(_lastParty));
				if (_lastHero != null) sb.Append(DumpValidation(_lastHero));
				sb.AppendLine("[BREADCRUMBS]");
				foreach (string b in BreadcrumbSnapshot()) sb.AppendLine(b);
				sb.AppendLine();
				sb.AppendLine("[EXCEPTION]");
				sb.AppendLine(ExceptionText(ex));
				sb.AppendLine("============================================================");
				Append(sb.ToString());
			}
			catch (Exception loggerEx)
			{
				EmergencyWrite("REPORT_EXCEPTION FAILED: " + Flatten(loggerEx) + " | ORIGINAL: " + Flatten(ex));
			}
		}

		internal static string DumpHero(object hero)
		{
			StringBuilder sb = Section("HERO SNAPSHOT");
			if (hero == null) { sb.AppendLine("Hero: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "EncyclopediaLink", "IsAlive", "IsDead", "HeroState", "Occupation", "Age", "IsFemale", "IsChild", "IsLord", "IsPartyLeader", "IsPrisoner" }) Prop(sb, "Hero." + p, hero, p);
			foreach (string p in new[] { "CharacterObject", "Template", "Clan", "MapFaction", "HomeSettlement", "StayingInSettlement", "PartyBelongedTo", "PartyBelongedToAsPrisoner", "Spouse", "Father", "Mother" }) Prop(sb, "Hero." + p, hero, p);
			object ch = ReadValue(hero, "CharacterObject").Value;
			if (ch != null)
			{
				foreach (string p in new[] { "StringId", "Name", "IsHero", "IsOriginalCharacter", "OriginalCharacter", "HeroObject" }) Prop(sb, "Hero.CharacterObject." + p, ch, p);
			}
			ListProp(sb, "Hero.Children", hero, "Children", 40);
			ListProp(sb, "Hero.Siblings", hero, "Siblings", 40);
			ListProp(sb, "Hero.OwnedCaravans", hero, "OwnedCaravans", 30);
			ListProp(sb, "Hero.OwnedAlleys", hero, "OwnedAlleys", 30);
			ListProp(sb, "Hero.OwnedWorkshops", hero, "OwnedWorkshops", 30);
			return sb.ToString();
		}

		internal static string DumpClan(object clan)
		{
			StringBuilder sb = Section("CLAN SNAPSHOT");
			if (clan == null) { sb.AppendLine("Clan: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "Leader", "Kingdom", "MapFaction", "Culture", "Tier", "IsRebelClan", "IsMinorFaction", "IsEliminated", "IsUnderMercenaryService", "IsBanditFaction" }) Prop(sb, "Clan." + p, clan, p);
			ListProp(sb, "Clan.Heroes", clan, "Heroes", 80);
			ListProp(sb, "Clan.Settlements", clan, "Settlements", 50);
			ListProp(sb, "Clan.Fiefs", clan, "Fiefs", 50);
			return sb.ToString();
		}

		internal static string DumpKingdom(object kingdom)
		{
			StringBuilder sb = Section("KINGDOM SNAPSHOT");
			if (kingdom == null) { sb.AppendLine("Kingdom: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "Leader", "RulingClan", "Culture", "IsEliminated" }) Prop(sb, "Kingdom." + p, kingdom, p);
			ListProp(sb, "Kingdom.Clans", kingdom, "Clans", 80);
			ListProp(sb, "Kingdom.Settlements", kingdom, "Settlements", 60);
			return sb.ToString();
		}

		internal static string DumpSettlement(object settlement)
		{
			StringBuilder sb = Section("SETTLEMENT SNAPSHOT");
			if (settlement == null) { sb.AppendLine("Settlement: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "OwnerClan", "MapFaction", "Culture", "IsTown", "IsCastle", "IsVillage", "IsHideout" }) Prop(sb, "Settlement." + p, settlement, p);
			ListProp(sb, "Settlement.Notables", settlement, "Notables", 50);
			return sb.ToString();
		}

		internal static string DumpParty(object party)
		{
			StringBuilder sb = Section("PARTY SNAPSHOT");
			if (party == null) { sb.AppendLine("Party: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "LeaderHero", "ActualClan", "MapFaction", "CurrentSettlement", "HomeSettlement", "MapEvent", "IsActive", "IsLordParty", "IsCaravan" }) Prop(sb, "Party." + p, party, p);
			Prop(sb, "Party.MemberRoster", party, "MemberRoster");
			Prop(sb, "Party.PrisonRoster", party, "PrisonRoster");
			return sb.ToString();
		}

		internal static string DumpValidation(object hero)
		{
			StringBuilder sb = Section("REFERENCE VALIDATION");
			List<string> issues = ValidateHero(hero);
			if (issues.Count == 0) sb.AppendLine("No obvious inconsistencies detected by defensive checks.");
			else foreach (string issue in issues) sb.AppendLine("WARNING: " + issue);
			return sb.ToString();
		}

		private static List<string> ValidateHero(object hero)
		{
			List<string> w = new List<string>();
			if (hero == null) { w.Add("Hero reference is null."); return w; }
			string heroId = StringId(hero);
			if (string.IsNullOrEmpty(heroId) || heroId.StartsWith("<")) w.Add("Hero.StringId is null/empty/unreadable: " + heroId);

			ReadResult link = ReadValue(hero, "EncyclopediaLink");
			if (link.Error != null) w.Add("Hero.EncyclopediaLink throws " + link.Error);
			else if (link.Value == null || string.IsNullOrEmpty(Convert.ToString(link.Value))) w.Add("Hero.EncyclopediaLink is null/empty.");

			object ch = ReadValue(hero, "CharacterObject").Value;
			if (ch == null) w.Add("Hero exists but CharacterObject is null.");
			else
			{
				object backlink = ReadValue(ch, "HeroObject").Value;
				if (backlink != null && !ReferenceEquals(backlink, hero)) w.Add("CharacterObject.HeroObject does not point back to this hero: " + SafeDescribe(backlink));
				bool isOriginal = AsBool(ReadValue(ch, "IsOriginalCharacter").Value, false);
				ReadResult origin = ReadValue(ch, "OriginalCharacter");
				if (!isOriginal && origin.Error == null && origin.Value == null) w.Add("Non-original CharacterObject has null OriginalCharacter/template.");
				if (origin.Error != null) w.Add("CharacterObject.OriginalCharacter throws " + origin.Error);
			}

			object clan = ReadValue(hero, "Clan").Value;
			if (clan != null)
			{
				IEnumerable heroes = ReadValue(clan, "Heroes").Value as IEnumerable;
				if (heroes != null && !ContainsReference(heroes, hero)) w.Add("Hero.Clan is set but Clan.Heroes does not contain this hero.");
				object leader = ReadValue(clan, "Leader").Value;
				bool eliminated = AsBool(ReadValue(clan, "IsEliminated").Value, false);
				if (!eliminated && leader == null) w.Add("Clan is not eliminated but Clan.Leader is null.");
				if (ReferenceEquals(leader, hero) && heroes != null && !ContainsReference(heroes, hero)) w.Add("Hero is Clan.Leader but is missing from Clan.Heroes.");
				object kingdom = ReadValue(clan, "Kingdom").Value;
				object mapFaction = ReadValue(hero, "MapFaction").Value;
				if (mapFaction != null && !ReferenceEquals(mapFaction, clan) && (kingdom == null || !ReferenceEquals(mapFaction, kingdom)))
					w.Add("Hero.MapFaction disagrees with both Hero.Clan and Clan.Kingdom: mapFaction=" + SafeDescribe(mapFaction) + " clan=" + SafeDescribe(clan) + " kingdom=" + SafeDescribe(kingdom));
			}

			bool prisoner = AsBool(ReadValue(hero, "IsPrisoner").Value, false);
			object prisonerParty = ReadValue(hero, "PartyBelongedToAsPrisoner").Value;
			if (prisoner && prisonerParty == null) w.Add("Hero.IsPrisoner=true but PartyBelongedToAsPrisoner is null.");
			if (!prisoner && prisonerParty != null) w.Add("Hero.IsPrisoner=false but PartyBelongedToAsPrisoner is non-null: " + SafeDescribe(prisonerParty));

			CheckRefId(w, hero, "HomeSettlement");
			CheckRefId(w, hero, "StayingInSettlement");
			CheckRefId(w, hero, "PartyBelongedTo");
			CheckRefId(w, hero, "PartyBelongedToAsPrisoner");
			CheckFamilyList(w, hero, "Children");
			CheckFamilyList(w, hero, "Siblings");
			CheckFamilySingle(w, hero, "Spouse");
			CheckFamilySingle(w, hero, "Father");
			CheckFamilySingle(w, hero, "Mother");

			int duplicates = CountHeroId(heroId);
			if (duplicates > 1) w.Add("Duplicate/suspicious Hero StringId detected: " + heroId + " appears " + duplicates + " times in accessible hero lists.");
			return w;
		}

		private static void CheckRefId(List<string> w, object owner, string property)
		{
			ReadResult r = ReadValue(owner, property);
			if (r.Error != null) { w.Add(property + " throws " + r.Error); return; }
			if (r.Value == null) return;
			string id = StringId(r.Value);
			if (string.IsNullOrEmpty(id) || id.StartsWith("<")) w.Add(property + " points to object with unreadable/empty StringId: " + SafeDescribe(r.Value));
		}

		private static void CheckFamilySingle(List<string> w, object hero, string property)
		{
			ReadResult r = ReadValue(hero, property);
			if (r.Error != null) w.Add("Hero." + property + " throws " + r.Error);
			else if (r.Value != null && StringId(r.Value).StartsWith("<")) w.Add("Hero." + property + " is malformed/unidentifiable: " + SafeDescribe(r.Value));
		}

		private static void CheckFamilyList(List<string> w, object hero, string property)
		{
			ReadResult r = ReadValue(hero, property);
			if (r.Error != null) { w.Add("Hero." + property + " throws " + r.Error); return; }
			IEnumerable e = r.Value as IEnumerable;
			if (e == null) return;
			int i = 0;
			try
			{
				foreach (object x in e)
				{
					if (x == null) w.Add("Hero." + property + " contains null at index " + i + ".");
					else if (StringId(x).StartsWith("<")) w.Add("Hero." + property + " contains malformed reference at index " + i + ": " + SafeDescribe(x));
					i++;
				}
			}
			catch (Exception ex) { w.Add("Enumerating Hero." + property + " throws " + ex.GetType().Name + ": " + ex.Message); }
		}

		private static int CountHeroId(string id)
		{
			if (string.IsNullOrEmpty(id) || id.StartsWith("<")) return 0;
			HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			foreach (string member in new[] { "AllAliveHeroes", "DeadOrDisabledHeroes" })
			{
				object value = ReadStaticValue(typeof(Hero), member).Value;
				IEnumerable e = value as IEnumerable;
				if (e == null) continue;
				try { foreach (object h in e) if (h != null && StringId(h) == id) seen.Add(h); } catch { }
			}
			return seen.Count;
		}

		private static bool ContainsReference(IEnumerable e, object target)
		{
			try { foreach (object x in e) if (ReferenceEquals(x, target)) return true; } catch { }
			return false;
		}

		private static void CaptureRelatedContext(object hero)
		{
			if (hero == null) return;
			object clan = ReadValue(hero, "Clan").Value;
			object party = ReadValue(hero, "PartyBelongedTo").Value ?? ReadValue(hero, "PartyBelongedToAsPrisoner").Value;
			object settlement = ReadValue(hero, "StayingInSettlement").Value ?? ReadValue(hero, "HomeSettlement").Value;
			if (clan != null) _lastClan = clan;
			if (party != null) _lastParty = party;
			if (settlement != null) _lastSettlement = settlement;
			object kingdom = clan == null ? null : ReadValue(clan, "Kingdom").Value;
			if (kingdom != null) _lastKingdom = kingdom;
		}

		public static string Status()
		{
			return "DIAG_V171 initialized=" + _initialized + ", patchedMethods=" + _patchedCount + ", mode=surgical, currentPage=" + _currentPage + ", lastLink=" + _lastLink + ", log=" + LogPath;
		}

		public static string DumpBreadcrumbsToLog()
		{
			StringBuilder sb = Section("BREADCRUMBS");
			foreach (string b in BreadcrumbSnapshot()) sb.AppendLine(b);
			Append(sb.ToString());
			return "BREADCRUMBS_WRITTEN: " + LogPath;
		}

		public static string ManualDumpHero(string id)
		{
			object hero = FindByStringId(typeof(Hero), id, new[] { "AllAliveHeroes", "DeadOrDisabledHeroes" });
			if (hero == null) return "HERO_NOT_FOUND: " + id;
			Append(DumpHero(hero) + DumpValidation(hero));
			return "HERO_DUMPED: " + SafeDescribe(hero) + " -> " + LogPath;
		}

		public static string ManualDumpClan(string id)
		{
			object clan = FindByStringId(typeof(Clan), id, new[] { "All" });
			if (clan == null) return "CLAN_NOT_FOUND: " + id;
			Append(DumpClan(clan));
			return "CLAN_DUMPED: " + SafeDescribe(clan) + " -> " + LogPath;
		}

		private static object FindByStringId(Type type, string id, string[] staticMembers)
		{
			foreach (string member in staticMembers)
			{
				IEnumerable e = ReadStaticValue(type, member).Value as IEnumerable;
				if (e == null) continue;
				try { foreach (object x in e) if (x != null && string.Equals(StringId(x), id, StringComparison.Ordinal)) return x; } catch { }
			}
			return null;
		}

		internal static string SafeDescribe(object obj)
		{
			if (obj == null) return "<null>";
			try
			{
				string type = obj.GetType().Name;
				string id = StringId(obj);
				string name = SafeText(ReadValue(obj, "Name"));
				return type + "{Name=" + name + ", StringId=" + id + "}";
			}
			catch (Exception ex) { return "<ERROR describing " + obj.GetType().FullName + ": " + ex.GetType().Name + ">"; }
		}

		private static void Prop(StringBuilder sb, string label, object obj, string property)
		{
			ReadResult r = property == null ? new ReadResult(obj, null) : ReadValue(obj, property);
			if (r.Error != null) sb.AppendLine(label + ": <ERROR reading property: " + r.Error + ">");
			else if (r.Value == null) sb.AppendLine(label + ": <null>");
			else if (IsSimple(r.Value)) sb.AppendLine(label + ": " + SafeScalar(r.Value));
			else sb.AppendLine(label + ": " + SafeDescribe(r.Value));
		}

		private static void ListProp(StringBuilder sb, string label, object obj, string property, int max)
		{
			ReadResult r = ReadValue(obj, property);
			if (r.Error != null) { sb.AppendLine(label + ": <ERROR reading property: " + r.Error + ">"); return; }
			if (r.Value == null) { sb.AppendLine(label + ": <null>"); return; }
			IEnumerable e = r.Value as IEnumerable;
			if (e == null) { sb.AppendLine(label + ": <not enumerable> " + SafeDescribe(r.Value)); return; }
			List<string> items = new List<string>();
			try
			{
				foreach (object x in e)
				{
					items.Add(SafeDescribe(x));
					if (items.Count >= max) { items.Add("<truncated>"); break; }
				}
				sb.AppendLine(label + " (count shown=" + items.Count + "): " + string.Join(" | ", items.ToArray()));
			}
			catch (Exception ex) { sb.AppendLine(label + ": <ERROR enumerating: " + ex.GetType().Name + ": " + ex.Message + ">"); }
		}

		private static ReadResult ReadValue(object obj, string name)
		{
			if (obj == null) return new ReadResult(null, null);
			try
			{
				Type t = obj.GetType();
				PropertyInfo p = FindProperty(t, name);
				if (p != null) return new ReadResult(p.GetValue(obj, null), null);
				FieldInfo f = FindField(t, name);
				if (f != null) return new ReadResult(f.GetValue(obj), null);
				return new ReadResult(null, "member-not-found");
			}
			catch (TargetInvocationException tie) { return new ReadResult(null, Flatten(tie.InnerException ?? tie)); }
			catch (Exception ex) { return new ReadResult(null, Flatten(ex)); }
		}

		private static ReadResult ReadStaticValue(Type t, string name)
		{
			if (t == null) return new ReadResult(null, "type-null");
			try
			{
				PropertyInfo p = t.GetProperty(name, SF);
				if (p != null) return new ReadResult(p.GetValue(null, null), null);
				FieldInfo f = t.GetField(name, SF);
				if (f != null) return new ReadResult(f.GetValue(null), null);
				return new ReadResult(null, "member-not-found");
			}
			catch (Exception ex) { return new ReadResult(null, Flatten(ex)); }
		}

		private static PropertyInfo FindProperty(Type t, string name)
		{
			while (t != null) { PropertyInfo p = t.GetProperty(name, IF); if (p != null) return p; t = t.BaseType; }
			return null;
		}

		private static FieldInfo FindField(Type t, string name)
		{
			while (t != null)
			{
				FieldInfo f = t.GetField(name, IF) ?? t.GetField("_" + Char.ToLowerInvariant(name[0]) + name.Substring(1), IF);
				if (f != null) return f;
				t = t.BaseType;
			}
			return null;
		}

		private static string StringId(object obj)
		{
			if (obj == null) return "<null>";
			ReadResult r = ReadValue(obj, "StringId");
			if (r.Error != null) return "<ERROR:" + r.Error + ">";
			return r.Value == null ? "<null>" : Convert.ToString(r.Value);
		}

		private static string SafeText(ReadResult r)
		{
			if (r.Error != null) return "<ERROR:" + r.Error + ">";
			return r.Value == null ? "<null>" : SafeScalar(r.Value);
		}

		private static string SafeScalar(object value)
		{
			if (value == null) return "<null>";
			try { return Convert.ToString(value); } catch (Exception ex) { return "<ERROR ToString: " + ex.GetType().Name + ">"; }
		}

		private static bool IsSimple(object v)
		{
			if (v == null) return true;
			Type t = v.GetType();
			return t.IsPrimitive || t.IsEnum || v is string || v is decimal || v is DateTime;
		}

		private static bool AsBool(object v, bool fallback)
		{
			try { return v is bool ? (bool)v : Convert.ToBoolean(v); } catch { return fallback; }
		}

		private static string ArgsToLink(object[] args)
		{
			try
			{
				if (args == null || args.Length == 0) return "<no-args>";
				if (args.Length == 1) return args[0] == null ? "<null>" : Convert.ToString(args[0]);
				return (args[0] == null ? "<null>" : Convert.ToString(args[0])) + "-" + (args[1] == null ? "<null>" : Convert.ToString(args[1]));
			}
			catch { return "<ERROR building link>"; }
		}

		private static StringBuilder Section(string name)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine();
			sb.AppendLine("[" + name + "]");
			return sb;
		}

		private static void AddBreadcrumb(string text)
		{
			lock (Sync)
			{
				Breadcrumbs.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff") + " " + text);
				while (Breadcrumbs.Count > MaxBreadcrumbs) Breadcrumbs.Dequeue();
			}
		}

		private static string[] BreadcrumbSnapshot()
		{
			lock (Sync) return Breadcrumbs.ToArray();
		}

		private static void EnsureLogPath()
		{
			if (!string.IsNullOrEmpty(_logPath)) return;
			string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			DirectoryInfo d = new DirectoryInfo(asmDir);
			DirectoryInfo moduleRoot = d.Parent != null && d.Parent.Parent != null ? d.Parent.Parent : d;
			string logs = Path.Combine(moduleRoot.FullName, "Logs");
			Directory.CreateDirectory(logs);
			_logPath = Path.Combine(logs, "CharacterCrashGuard_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log");
		}

		private static void WriteSessionHeader()
		{
			StringBuilder sb = new StringBuilder(12000);
			sb.AppendLine("============================================================");
			sb.AppendLine("CHARACTER CRASH GUARD - DIAGNOSTIC REPORT");
			sb.AppendLine("============================================================");
			sb.AppendLine();
			sb.AppendLine("[SESSION]");
			sb.AppendLine("Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
			sb.AppendLine("Character Crash Guard: " + ModVersion);
			sb.AppendLine("Compiled target: Bannerlord " + TargetGameVersion);
			sb.AppendLine("Runtime game version: " + RuntimeVersion());
			sb.AppendLine("Process: " + Process.GetCurrentProcess().ProcessName + " pid=" + Process.GetCurrentProcess().Id);
			sb.AppendLine("Log: " + LogPath);
			sb.AppendLine();
			sb.AppendLine("[LOADED ASSEMBLIES AT INIT]");
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
			{
				try { sb.AppendLine("  - " + a.GetName().Name + " " + a.GetName().Version); } catch { }
			}
			Append(sb.ToString());
		}

		private static string RuntimeVersion()
		{
			try { return "CurrentVersion=" + MBSaveLoad.CurrentVersion + ", LastLoadedGameVersion=" + MBSaveLoad.LastLoadedGameVersion; }
			catch (Exception ex) { return "<ERROR: " + ex.GetType().Name + ">"; }
		}

		private static void Append(string text)
		{
			try
			{
				EnsureLogPath();
				lock (Sync) File.AppendAllText(_logPath, text.EndsWith("\r\n") ? text : text + "\r\n", Encoding.UTF8);
			}
			catch (Exception ex) { EmergencyWrite("LOG WRITE FAILED: " + Flatten(ex)); }
		}

		private static void WriteLine(string text) => Append("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + text);

		private static void EmergencyWrite(string text)
		{
			try
			{
				string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "CharacterCrashGuard_Emergency.log");
				Directory.CreateDirectory(Path.GetDirectoryName(p));
				File.AppendAllText(p, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + text + "\r\n");
			}
			catch { }
		}

		private static string FormatMethod(MethodBase m)
		{
			if (m == null) return "<unknown method>";
			try { return (m.DeclaringType == null ? "<global>" : m.DeclaringType.FullName) + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")"; }
			catch { return m.Name; }
		}

		private static string ExceptionText(Exception ex)
		{
			StringBuilder sb = new StringBuilder();
			int depth = 0;
			while (ex != null && depth < 12)
			{
				sb.AppendLine("--- Exception depth " + depth + " ---");
				sb.AppendLine("Type: " + ex.GetType().FullName);
				sb.AppendLine("Message: " + ex.Message);
				sb.AppendLine("StackTrace:");
				sb.AppendLine(ex.StackTrace ?? "<no stack>");
				ex = ex.InnerException;
				depth++;
			}
			return sb.ToString();
		}

		private static string Flatten(Exception ex)
		{
			if (ex == null) return "<null-exception>";
			try
			{
				List<string> parts = new List<string>();
				int n = 0;
				while (ex != null && n++ < 8) { parts.Add(ex.GetType().Name + ": " + ex.Message); ex = ex.InnerException; }
				return string.Join(" -> ", parts.ToArray());
			}
			catch { return "<error flattening exception>"; }
		}

		private sealed class ReadResult
		{
			public readonly object Value;
			public readonly string Error;
			public ReadResult(object value, string error) { Value = value; Error = error; }
		}

		private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
		{
			public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
			public new bool Equals(object x, object y) => ReferenceEquals(x, y);
			public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
		}
	}
}
