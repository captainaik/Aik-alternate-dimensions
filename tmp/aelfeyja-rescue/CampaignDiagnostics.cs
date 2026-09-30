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
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	/// <summary>
	/// General, non-destructive campaign-object diagnostic layer.
	/// All inspection is defensive: each property read is isolated so a corrupt
	/// object cannot crash the reporter while it is trying to describe the object.
	/// </summary>
	public sealed class CampaignDiagnosticsSubModule : MBSubModuleBase
	{
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			CampaignDiagnostics.Initialize();
		}

		protected override void OnGameStart(TaleWorlds.Core.Game game, TaleWorlds.Core.IGameStarter gameStarterObject)
		{
			base.OnGameStart(game, gameStarterObject);
			CampaignDiagnostics.PatchAvailableTargets("OnGameStart");
		}

		protected override void OnBeforeInitialModuleScreenSetAsRoot()
		{
			base.OnBeforeInitialModuleScreenSetAsRoot();
			CampaignDiagnostics.PatchAvailableTargets("OnBeforeInitialModuleScreenSetAsRoot");
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
			return CampaignDiagnostics.ManualDump("Hero", args[0]);
		}

		[CommandLineFunctionality.CommandLineArgumentFunction("diag_dump_clan", "rescue")]
		public static string DumpClan(List<string> args)
		{
			if (args == null || args.Count == 0) return "Usage: rescue.diag_dump_clan <Clan StringId>";
			return CampaignDiagnostics.ManualDump("Clan", args[0]);
		}
	}

	internal static class CampaignDiagnostics
	{
		private const string ModVersion = "v1.7.0";
		private const string TargetGameVersion = "v1.5.3.122374-beta";
		private const string HarmonyId = "aik.bannerlord.character_crash_guard.campaign_diagnostics.v17";
		private const int MaxBreadcrumbs = 100;
		private static readonly BindingFlags IF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
		private static readonly BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static readonly object Sync = new object();
		private static readonly Queue<string> Breadcrumbs = new Queue<string>();
		private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
		private static readonly Dictionary<string, DateTime> RecentWarnings = new Dictionary<string, DateTime>(StringComparer.Ordinal);
		private static Harmony _harmony;
		private static bool _initialized;
		private static int _patchedCount;
		private static string _logPath;
		private static string _lastLink = "<none>";
		private static string _lastContext = "<none>";
		private static string _lastSourcePage = "<unknown>";
		private static object _lastHero;
		private static object _lastClan;
		private static object _lastKingdom;
		private static object _lastSettlement;
		private static object _lastParty;
		private static int _lastExceptionRef;
		private static DateTime _lastExceptionAtUtc = DateTime.MinValue;

		public static string LogPath
		{
			get
			{
				EnsureLogPath();
				return _logPath;
			}
		}

		public static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;
			try
			{
				EnsureLogPath();
				_harmony = new Harmony(HarmonyId);
				WriteSessionHeader();
				AddBreadcrumb("Character Crash Guard campaign diagnostics initialized " + ModVersion);
				PatchAvailableTargets("OnSubModuleLoad");
			}
			catch (Exception ex)
			{
				EmergencyWrite("DIAGNOSTIC INITIALIZATION FAILED: " + Flatten(ex));
			}
		}

		public static void PatchAvailableTargets(string phase)
		{
			if (!_initialized || _harmony == null) return;
			try
			{
				PatchHeroVm();
				PatchEncyclopediaManager();
				PatchLinkItem("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Items.EncyclopediaFactionVM");
				PatchLinkItem("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Items.EncyclopediaSettlementVM");
				PatchLinkItem("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Items.EncyclopediaDwellingVM");
				PatchPage("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaHeroPageVM");
				PatchPage("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaClanPageVM");
				PatchPage("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaFactionPageVM");
				PatchPage("TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages.EncyclopediaSettlementPageVM");
				PatchEncyclopediaData();
				WriteDedup("PATCH_PHASE:" + phase + ":" + _patchedCount,
					"[PATCH STATUS]\r\nPhase: " + phase + "\r\nPatched methods: " + _patchedCount + "\r\n");
			}
			catch (Exception ex)
			{
				WriteLine("PATCH_AVAILABLE_EXCEPTION phase=" + phase + " error=" + Flatten(ex));
			}
		}

		private static void PatchHeroVm()
		{
			Type t = FindType("TaleWorlds.CampaignSystem.ViewModelCollection.HeroVM");
			if (t == null) return;
			MethodInfo execute = t.GetMethod("ExecuteLink", IF, null, Type.EmptyTypes, null);
			Patch(execute, nameof(HeroExecutePrefix), null, nameof(HeroExecuteFinalizer));
			foreach (ConstructorInfo ctor in t.GetConstructors(IF))
				Patch(ctor, nameof(HeroCtorPrefix), nameof(HeroCtorPostfix), nameof(HeroCtorFinalizer));
		}

		private static void PatchEncyclopediaManager()
		{
			Type t = FindType("TaleWorlds.CampaignSystem.Encyclopedia.EncyclopediaManager");
			if (t == null) return;
			foreach (MethodInfo m in t.GetMethods(IF).Where(x => x.Name == "GoToLink"))
				Patch(m, nameof(GoToLinkPrefix), null, nameof(GenericFinalizer));
		}

		private static void PatchLinkItem(string typeName)
		{
			Type t = FindType(typeName);
			if (t == null) return;
			foreach (MethodInfo m in t.GetMethods(IF).Where(x => x.Name == "ExecuteLink"))
				Patch(m, nameof(GenericLinkPrefix), null, nameof(GenericFinalizer));
		}

		private static void PatchPage(string typeName)
		{
			Type t = FindType(typeName);
			if (t == null) return;
			foreach (ConstructorInfo ctor in t.GetConstructors(IF))
				Patch(ctor, null, nameof(PageCtorPostfix), nameof(GenericFinalizer));
			MethodInfo refresh = t.GetMethod("Refresh", IF, null, Type.EmptyTypes, null);
			Patch(refresh, nameof(PageRefreshPrefix), null, nameof(GenericFinalizer));
		}

		private static void PatchEncyclopediaData()
		{
			Type t = FindType("SandBox.GauntletUI.Encyclopedia.EncyclopediaData");
			if (t == null) return;
			foreach (MethodInfo m in t.GetMethods(IF).Where(x => x.Name == "ExecuteLink"))
				Patch(m, nameof(EncyclopediaDataPrefix), null, nameof(GenericFinalizer));
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

		// ---------------- Harmony hooks ----------------
		public static void HeroExecutePrefix(object __instance, MethodBase __originalMethod)
		{
			try
			{
				object hero = ReadValue(__instance, "Hero").Value;
				_lastHero = hero;
				object clan = ReadValue(hero, "Clan").Value;
				if (clan != null) _lastClan = clan;
				string link = SafeText(ReadValue(hero, "EncyclopediaLink"));
				_lastLink = link;
				_lastContext = "HeroVM.ExecuteLink -> " + SafeDescribe(hero);
				AddBreadcrumb("Hero link clicked: " + SafeDescribe(hero) + " link=" + link);

				StringBuilder sb = NewSection("ENCYCLOPEDIA EVENT");
				sb.AppendLine("Event: HeroVM.ExecuteLink");
				sb.AppendLine("Source page: " + _lastSourcePage);
				sb.AppendLine("Target type: Hero");
				sb.AppendLine("Target: " + SafeDescribe(hero));
				sb.AppendLine("Target link: " + link);
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

		public static Exception HeroExecuteFinalizer(object __instance, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception != null) ReportException(__exception, __originalMethod, __instance, "HeroVM.ExecuteLink");
			return __exception;
		}

		public static void HeroCtorPrefix(object[] __args, MethodBase __originalMethod)
		{
			try
			{
				object hero = __args != null && __args.Length > 0 ? __args[0] : null;
				if (hero != null)
				{
					_lastHero = hero;
					AddBreadcrumb("HeroVM creating: " + SafeDescribe(hero));
				}
			}
			catch { }
		}

		public static void HeroCtorPostfix(object __instance, object[] __args, MethodBase __originalMethod)
		{
			try
			{
				object hero = ReadValue(__instance, "Hero").Value ?? (__args != null && __args.Length > 0 ? __args[0] : null);
				if (hero != null) _lastHero = hero;
			}
			catch { }
		}

		public static Exception HeroCtorFinalizer(object __instance, object[] __args, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception != null)
			{
				object hero = __args != null && __args.Length > 0 ? __args[0] : null;
				if (hero != null) _lastHero = hero;
				ReportException(__exception, __originalMethod, __instance, "HeroVM constructor");
			}
			return __exception;
		}

		public static void GoToLinkPrefix(object[] __args, MethodBase __originalMethod)
		{
			try
			{
				string link;
				if (__args != null && __args.Length == 1)
					link = __args[0] == null ? "<null>" : __args[0].ToString();
				else if (__args != null && __args.Length >= 2)
					link = (__args[0] == null ? "<null>" : __args[0].ToString()) + "-" + (__args[1] == null ? "<null>" : __args[1].ToString());
				else link = "<no-args>";
				_lastLink = link;
				_lastContext = "EncyclopediaManager.GoToLink(" + link + ")";
				AddBreadcrumb("Encyclopedia GoToLink: " + link);
				WriteDedup("NAV_MANAGER:" + link + ":" + DateTime.Now.ToString("HHmmss"),
					"[ENCYCLOPEDIA NAVIGATION]\r\nSource page: " + _lastSourcePage + "\r\nTarget link: " + link + "\r\nMethod: " + FormatMethod(__originalMethod) + "\r\n");
			}
			catch (Exception ex) { WriteLine("GoToLinkPrefix diagnostic failure: " + Flatten(ex)); }
		}

		public static void GenericLinkPrefix(object __instance, object[] __args, MethodBase __originalMethod)
		{
			try
			{
				object obj = FirstCampaignObject(__instance);
				CaptureObjectContext(obj);
				string args = ArgsText(__args);
				_lastContext = __originalMethod.DeclaringType.FullName + ".ExecuteLink -> " + SafeDescribe(obj);
				AddBreadcrumb("Link VM: " + SafeDescribe(obj) + " args=" + args);
				WriteDedup("LINKVM:" + SafeDescribe(obj) + ":" + args + ":" + DateTime.Now.ToString("HHmmss"),
					"[ENCYCLOPEDIA NAVIGATION]\r\nSource page: " + _lastSourcePage + "\r\nSource VM: " + __originalMethod.DeclaringType.FullName + "\r\nTarget object: " + SafeDescribe(obj) + "\r\nArguments: " + args + "\r\n");
			}
			catch (Exception ex) { WriteLine("GenericLinkPrefix diagnostic failure: " + Flatten(ex)); }
		}

		public static void EncyclopediaDataPrefix(object __instance, object[] __args, MethodBase __originalMethod)
		{
			try
			{
				object active = ReadValue(__instance, "_activeDatasource").Value;
				string previous = SafeText(ReadValue(__instance, "_previousPageID"));
				object target = __args != null && __args.Length > 1 ? __args[1] : null;
				string pageId = __args != null && __args.Length > 0 ? Convert.ToString(__args[0]) : "<unknown>";
				_lastSourcePage = DescribePage(active, previous);
				CaptureObjectContext(target);
				_lastContext = "EncyclopediaData.ExecuteLink " + pageId + " -> " + SafeDescribe(target);
				AddBreadcrumb("Opened encyclopedia target: page=" + pageId + " object=" + SafeDescribe(target));
				Append("[ENCYCLOPEDIA NAVIGATION]\r\nSource page: " + _lastSourcePage + "\r\nTarget page id: " + pageId + "\r\nTarget object: " + SafeDescribe(target) + "\r\nLast link: " + _lastLink + "\r\n");
			}
			catch (Exception ex) { WriteLine("EncyclopediaDataPrefix diagnostic failure: " + Flatten(ex)); }
		}

		public static void PageCtorPostfix(object __instance, MethodBase __originalMethod)
		{
			try
			{
				object obj = FirstCampaignObject(__instance);
				CaptureObjectContext(obj);
				_lastSourcePage = __originalMethod.DeclaringType.Name + " :: " + SafeDescribe(obj);
				AddBreadcrumb("Page created: " + _lastSourcePage);
			}
			catch { }
		}

		public static void PageRefreshPrefix(object __instance, MethodBase __originalMethod)
		{
			try
			{
				object obj = FirstCampaignObject(__instance);
				CaptureObjectContext(obj);
				_lastSourcePage = __originalMethod.DeclaringType.Name + " :: " + SafeDescribe(obj);
				AddBreadcrumb("Page refresh: " + _lastSourcePage);
			}
			catch { }
		}

		public static Exception GenericFinalizer(object __instance, MethodBase __originalMethod, Exception __exception)
		{
			if (__exception != null) ReportException(__exception, __originalMethod, __instance, "Encyclopedia/navigation hook");
			return __exception;
		}

		// ---------------- Reports ----------------
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
				StringBuilder sb = new StringBuilder(16384);
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
				sb.AppendLine("Last encyclopedia source: " + _lastSourcePage);
				sb.AppendLine("Last link: " + _lastLink);
				sb.AppendLine("Last hero: " + SafeDescribe(_lastHero));
				sb.AppendLine("Last clan: " + SafeDescribe(_lastClan));
				sb.AppendLine("Last kingdom: " + SafeDescribe(_lastKingdom));
				sb.AppendLine("Last settlement: " + SafeDescribe(_lastSettlement));
				sb.AppendLine("Last party: " + SafeDescribe(_lastParty));
				sb.AppendLine();
				sb.AppendLine("[LOADED MODULES / ASSEMBLIES]");
				sb.AppendLine(ModuleAndAssemblyList());
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
			StringBuilder sb = NewSection("HERO SNAPSHOT");
			if (hero == null) { sb.AppendLine("Hero: <null>"); return sb.ToString(); }
			Prop(sb, "Hero", hero, null);
			foreach (string p in new[] { "Name", "StringId", "EncyclopediaLink", "IsAlive", "IsDead", "HeroState", "Occupation", "Age", "IsFemale", "IsChild", "IsLord", "IsPartyLeader", "IsPrisoner" }) Prop(sb, "Hero." + p, hero, p);
			foreach (string p in new[] { "CharacterObject", "Template", "Clan", "MapFaction", "HomeSettlement", "StayingInSettlement", "PartyBelongedTo", "PartyBelongedToAsPrisoner", "Spouse", "Father", "Mother" }) Prop(sb, "Hero." + p, hero, p);
			object ch = ReadValue(hero, "CharacterObject").Value;
			if (ch != null)
			{
				Prop(sb, "Hero.CharacterObject.StringId", ch, "StringId");
				Prop(sb, "Hero.CharacterObject.Name", ch, "Name");
				Prop(sb, "Hero.CharacterObject.IsHero", ch, "IsHero");
				Prop(sb, "Hero.CharacterObject.IsOriginalCharacter", ch, "IsOriginalCharacter");
				Prop(sb, "Hero.CharacterObject.OriginalCharacter", ch, "OriginalCharacter");
				Prop(sb, "Hero.CharacterObject.HeroObject", ch, "HeroObject");
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
			StringBuilder sb = NewSection("CLAN SNAPSHOT");
			if (clan == null) { sb.AppendLine("Clan: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "Leader", "Kingdom", "MapFaction", "Culture", "Tier", "IsRebelClan", "IsMinorFaction", "IsEliminated", "IsUnderMercenaryService", "IsBanditFaction" }) Prop(sb, "Clan." + p, clan, p);
			ListProp(sb, "Clan.Heroes", clan, "Heroes", 60);
			ListProp(sb, "Clan.Settlements", clan, "Settlements", 40);
			ListProp(sb, "Clan.Fiefs", clan, "Fiefs", 40);
			return sb.ToString();
		}

		internal static string DumpKingdom(object kingdom)
		{
			StringBuilder sb = NewSection("KINGDOM SNAPSHOT");
			if (kingdom == null) { sb.AppendLine("Kingdom: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "Leader", "RulingClan", "Culture", "IsEliminated" }) Prop(sb, "Kingdom." + p, kingdom, p);
			ListProp(sb, "Kingdom.Clans", kingdom, "Clans", 80);
			ListProp(sb, "Kingdom.Settlements", kingdom, "Settlements", 60);
			return sb.ToString();
		}

		internal static string DumpSettlement(object settlement)
		{
			StringBuilder sb = NewSection("SETTLEMENT SNAPSHOT");
			if (settlement == null) { sb.AppendLine("Settlement: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "OwnerClan", "MapFaction", "Culture", "IsTown", "IsCastle", "IsVillage", "IsHideout" }) Prop(sb, "Settlement." + p, settlement, p);
			ListProp(sb, "Settlement.Notables", settlement, "Notables", 50);
			return sb.ToString();
		}

		internal static string DumpParty(object party)
		{
			StringBuilder sb = NewSection("PARTY SNAPSHOT");
			if (party == null) { sb.AppendLine("Party: <null>"); return sb.ToString(); }
			foreach (string p in new[] { "Name", "StringId", "LeaderHero", "ActualClan", "MapFaction", "CurrentSettlement", "HomeSettlement", "MapEvent", "IsActive", "IsLordParty", "IsCaravan" }) Prop(sb, "Party." + p, party, p);
			Prop(sb, "Party.MemberRoster", party, "MemberRoster");
			Prop(sb, "Party.PrisonRoster", party, "PrisonRoster");
			return sb.ToString();
		}

		internal static string DumpValidation(object hero)
		{
			StringBuilder sb = NewSection("REFERENCE VALIDATION");
			List<string> issues = ValidateHero(hero);
			if (issues.Count == 0) sb.AppendLine("No obvious inconsistencies detected by defensive checks.");
			else foreach (string i in issues) sb.AppendLine("WARNING: " + i);
			return sb.ToString();
		}

		private static List<string> ValidateHero(object hero)
		{
			List<string> w = new List<string>();
			if (hero == null) { w.Add("Hero reference is null."); return w; }
			string heroId = StringId(hero);
			if (string.IsNullOrEmpty(heroId) || heroId.StartsWith("<")) w.Add("Hero.StringId is null/empty/unreadable: " + heroId);
			SafeValue link = ReadValue(hero, "EncyclopediaLink");
			if (link.Error != null) w.Add("Hero.EncyclopediaLink getter failed: " + link.Error);
			else if (link.Value == null || string.IsNullOrEmpty(Convert.ToString(link.Value))) w.Add("Hero.EncyclopediaLink is null/empty.");

			SafeValue cv = ReadValue(hero, "CharacterObject");
			object ch = cv.Value;
			if (cv.Error != null) w.Add("Hero.CharacterObject getter failed: " + cv.Error);
			else if (ch == null) w.Add("Hero exists but CharacterObject is null.");
			else
			{
				string cid = StringId(ch);
				if (string.IsNullOrEmpty(cid) || cid.StartsWith("<")) w.Add("CharacterObject.StringId is null/empty/unreadable: " + cid);
				SafeValue ho = ReadValue(ch, "HeroObject");
				if (ho.Error != null) w.Add("CharacterObject.HeroObject read failed: " + ho.Error);
				else if (ho.Value != null && !ReferenceEquals(ho.Value, hero)) w.Add("CharacterObject.HeroObject points to a different hero: " + SafeDescribe(ho.Value));
				SafeValue orig = ReadValue(ch, "OriginalCharacter");
				if (orig.Error != null) w.Add("CharacterObject.OriginalCharacter read failed: " + orig.Error);
				// Null OriginalCharacter can be legitimate for original objects; do not flag it by itself.
			}

			object clan = ReadValue(hero, "Clan").Value;
			if (clan != null)
			{
				string clanId = StringId(clan);
				if (string.IsNullOrEmpty(clanId) || clanId.StartsWith("<")) w.Add("Hero.Clan has invalid StringId: " + clanId);
				SafeValue heroes = ReadValue(clan, "Heroes");
				if (heroes.Error != null) w.Add("Clan.Heroes read failed: " + heroes.Error);
				else if (heroes.Value is IEnumerable && !ContainsReferenceOrId((IEnumerable)heroes.Value, hero)) w.Add("Hero has Clan but Clan.Heroes does not contain this hero.");
				object leader = ReadValue(clan, "Leader").Value;
				bool eliminated = Bool(ReadValue(clan, "IsEliminated").Value);
				if (leader == null && !eliminated) w.Add("Clan has no valid leader but is not marked eliminated.");
				bool heroLeader = Bool(ReadValue(hero, "IsClanLeader").Value);
				if (heroLeader && !ReferenceEquals(leader, hero)) w.Add("Hero.IsClanLeader is true but Clan.Leader points elsewhere: " + SafeDescribe(leader));
				object kingdom = ReadValue(clan, "Kingdom").Value;
				object mapFaction = ReadValue(hero, "MapFaction").Value;
				if (kingdom != null && mapFaction != null && !ReferenceEquals(kingdom, mapFaction) && !ReferenceEquals(clan, mapFaction))
					w.Add("Clan.Kingdom and Hero.MapFaction disagree: kingdom=" + SafeDescribe(kingdom) + " mapFaction=" + SafeDescribe(mapFaction));
			}

			bool prisoner = Bool(ReadValue(hero, "IsPrisoner").Value);
			object prisonerParty = ReadValue(hero, "PartyBelongedToAsPrisoner").Value;
			if (prisoner && prisonerParty == null) w.Add("Hero.IsPrisoner is true but PartyBelongedToAsPrisoner is null.");
			if (!prisoner && prisonerParty != null) w.Add("Hero is not marked prisoner but PartyBelongedToAsPrisoner is set: " + SafeDescribe(prisonerParty));
			object party = ReadValue(hero, "PartyBelongedTo").Value;
			if (party != null && StringId(party).StartsWith("<")) w.Add("Hero.PartyBelongedTo has invalid StringId: " + SafeDescribe(party));
			bool partyLeader = Bool(ReadValue(hero, "IsPartyLeader").Value);
			if (partyLeader && party != null)
			{
				object pl = ReadValue(party, "LeaderHero").Value;
				if (pl != null && !ReferenceEquals(pl, hero)) w.Add("Hero.IsPartyLeader is true but Party.LeaderHero differs: " + SafeDescribe(pl));
			}
			foreach (string sprop in new[] { "HomeSettlement", "StayingInSettlement" })
			{
				SafeValue sv = ReadValue(hero, sprop);
				if (sv.Error != null) w.Add("Hero." + sprop + " read failed: " + sv.Error);
				else if (sv.Value != null && StringId(sv.Value).StartsWith("<")) w.Add("Hero." + sprop + " has invalid StringId: " + SafeDescribe(sv.Value));
			}
			foreach (string fprop in new[] { "Children", "Siblings" })
			{
				SafeValue fv = ReadValue(hero, fprop);
				if (fv.Error != null) { w.Add("Hero." + fprop + " read failed: " + fv.Error); continue; }
				if (fv.Value is IEnumerable)
				{
					int n = 0;
					foreach (object x in (IEnumerable)fv.Value)
					{
						if (x == null) { w.Add("Hero." + fprop + " contains a null reference."); break; }
						if (++n > 100) break;
					}
				}
			}
			int dupes = DuplicateHeroIdCount(heroId);
			if (dupes > 1) w.Add("Duplicate Hero StringId detected: '" + heroId + "' count=" + dupes);
			return w;
		}

		// ---------------- Context / helpers ----------------
		private static object FirstCampaignObject(object vm)
		{
			if (vm == null) return null;
			foreach (string n in new[] { "Hero", "Settlement", "Faction", "Clan", "Kingdom", "_hero", "_settlement", "_faction", "_clan", "_kingdom", "Obj", "_obj" })
			{
				SafeValue v = ReadValue(vm, n);
				if (v.Value != null && LooksCampaignObject(v.Value)) return v.Value;
			}
			return null;
		}

		private static bool LooksCampaignObject(object o)
		{
			if (o == null) return false;
			string n = o.GetType().FullName ?? "";
			return n.Contains("Hero") || n.Contains("Clan") || n.Contains("Kingdom") || n.Contains("Settlement") || n.Contains("MobileParty") || n.Contains("CharacterObject");
		}

		private static void CaptureObjectContext(object o)
		{
			if (o == null) return;
			string n = o.GetType().FullName ?? "";
			if (n.EndsWith(".Hero", StringComparison.Ordinal)) { _lastHero = o; object c = ReadValue(o, "Clan").Value; if (c != null) _lastClan = c; }
			else if (n.EndsWith(".Clan", StringComparison.Ordinal)) _lastClan = o;
			else if (n.EndsWith(".Kingdom", StringComparison.Ordinal)) _lastKingdom = o;
			else if (n.Contains("Settlement")) _lastSettlement = o;
			else if (n.Contains("MobileParty")) _lastParty = o;
		}

		private static string DescribePage(object active, string previous)
		{
			if (active == null) return string.IsNullOrEmpty(previous) ? "<unknown>" : previous;
			object obj = FirstCampaignObject(active);
			return active.GetType().Name + " :: " + SafeDescribe(obj) + (string.IsNullOrEmpty(previous) ? "" : " previousId=" + previous);
		}

		private static bool ContainsReferenceOrId(IEnumerable list, object target)
		{
			string id = StringId(target);
			int n = 0;
			foreach (object x in list)
			{
				if (ReferenceEquals(x, target)) return true;
				if (x != null && !id.StartsWith("<") && string.Equals(StringId(x), id, StringComparison.Ordinal)) return true;
				if (++n > 10000) break;
			}
			return false;
		}

		private static int DuplicateHeroIdCount(string id)
		{
			if (string.IsNullOrEmpty(id) || id.StartsWith("<")) return 0;
			Type ht = FindType("TaleWorlds.CampaignSystem.Hero");
			if (ht == null) return 0;
			HashSet<object> seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
			int count = 0;
			foreach (string p in new[] { "AllAliveHeroes", "DeadOrDisabledHeroes", "AllHeroes" })
			{
				SafeValue v = ReadStaticValue(ht, p);
				if (!(v.Value is IEnumerable)) continue;
				foreach (object h in (IEnumerable)v.Value)
				{
					if (h == null || !seen.Add(h)) continue;
					if (string.Equals(StringId(h), id, StringComparison.Ordinal)) count++;
				}
			}
			return count;
		}

		public static string ManualDump(string kind, string id)
		{
			try
			{
				object o = FindCampaignObject(kind, id);
				if (o == null) return "NOT_FOUND: " + kind + " " + id;
				CaptureObjectContext(o);
				string dump;
				if (kind == "Hero") dump = DumpHero(o) + DumpValidation(o);
				else dump = DumpClan(o);
				Append("[MANUAL DUMP]\r\n" + dump);
				return "DUMPED " + SafeDescribe(o) + " -> " + LogPath;
			}
			catch (Exception ex) { return "DUMP_ERROR: " + Flatten(ex); }
		}

		private static object FindCampaignObject(string kind, string id)
		{
			string typeName = kind == "Hero" ? "TaleWorlds.CampaignSystem.Hero" : "TaleWorlds.CampaignSystem.Clan";
			Type t = FindType(typeName);
			if (t == null) return null;
			foreach (string prop in kind == "Hero" ? new[] { "AllAliveHeroes", "DeadOrDisabledHeroes", "AllHeroes" } : new[] { "All", "NonBanditFactions" })
			{
				SafeValue v = ReadStaticValue(t, prop);
				if (!(v.Value is IEnumerable)) continue;
				foreach (object o in (IEnumerable)v.Value)
					if (o != null && string.Equals(StringId(o), id, StringComparison.Ordinal)) return o;
			}
			return null;
		}

		private static StringBuilder NewSection(string title)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine();
			sb.AppendLine("[" + title + "]");
			return sb;
		}

		private static void Prop(StringBuilder sb, string label, object obj, string member)
		{
			if (member == null) { sb.AppendLine(label + ": " + SafeDescribe(obj)); return; }
			SafeValue v = ReadValue(obj, member);
			if (v.Error != null) sb.AppendLine(label + ": <ERROR reading property: " + v.Error + ">");
			else sb.AppendLine(label + ": " + SafeDescribe(v.Value));
		}

		private static void ListProp(StringBuilder sb, string label, object obj, string member, int limit)
		{
			SafeValue v = ReadValue(obj, member);
			if (v.Error != null) { sb.AppendLine(label + ": <ERROR reading property: " + v.Error + ">"); return; }
			if (v.Value == null) { sb.AppendLine(label + ": <null>"); return; }
			if (!(v.Value is IEnumerable)) { sb.AppendLine(label + ": " + SafeDescribe(v.Value)); return; }
			List<string> items = new List<string>();
			int total = 0;
			try
			{
				foreach (object x in (IEnumerable)v.Value)
				{
					total++;
					if (items.Count < limit) items.Add(SafeDescribe(x));
					if (total > 10000) { items.Add("<enumeration stopped at 10000>"); break; }
				}
				sb.AppendLine(label + " count=" + total + ": " + string.Join(" | ", items.ToArray()) + (total > limit ? " | ..." : ""));
			}
			catch (Exception ex) { sb.AppendLine(label + ": <ERROR enumerating: " + Flatten(ex) + ">"); }
		}

		internal static string SafeDescribe(object o)
		{
			if (o == null) return "<null>";
			try
			{
				Type t = o.GetType();
				if (o is string) return "\"" + o + "\"";
				if (t.IsPrimitive || t.IsEnum || o is decimal) return Convert.ToString(o);
				SafeValue name = ReadValue(o, "Name");
				SafeValue id = ReadValue(o, "StringId");
				string ns = name.Error != null ? "<ERROR:" + name.Error + ">" : SafeObjectText(name.Value);
				string ids = id.Error != null ? "<ERROR:" + id.Error + ">" : SafeObjectText(id.Value);
				if (name.Value != null || id.Value != null || name.Error != null || id.Error != null)
					return t.Name + "{Name=" + ns + ", StringId=" + ids + "}";
				return t.Name + "{" + SafeObjectText(o) + "}";
			}
			catch (Exception ex) { return "<ERROR describing object: " + Flatten(ex) + ">"; }
		}

		private static string SafeObjectText(object o)
		{
			if (o == null) return "<null>";
			try { return o.ToString(); } catch (Exception ex) { return "<ERROR ToString: " + ex.GetType().Name + ">"; }
		}

		private static string StringId(object o)
		{
			SafeValue v = ReadValue(o, "StringId");
			if (v.Error != null) return "<ERROR:" + v.Error + ">";
			if (v.Value == null) return "<null>";
			try { return Convert.ToString(v.Value); } catch { return "<unreadable>"; }
		}

		private static SafeValue ReadValue(object o, string name)
		{
			if (o == null) return new SafeValue(null, null);
			try
			{
				Type t = o.GetType();
				PropertyInfo p = FindProperty(t, name, IF);
				if (p != null)
				{
					try { return new SafeValue(p.GetValue(o, null), null); }
					catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); }
				}
				FieldInfo f = FindField(t, name, IF) ?? FindField(t, "_" + name, IF);
				if (f != null)
				{
					try { return new SafeValue(f.GetValue(o), null); }
					catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); }
				}
				return new SafeValue(null, "member-not-found");
			}
			catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); }
		}

		private static SafeValue ReadStaticValue(Type t, string name)
		{
			if (t == null) return new SafeValue(null, "type-null");
			try
			{
				PropertyInfo p = FindProperty(t, name, SF);
				if (p != null) { try { return new SafeValue(p.GetValue(null, null), null); } catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); } }
				FieldInfo f = FindField(t, name, SF);
				if (f != null) { try { return new SafeValue(f.GetValue(null), null); } catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); } }
				return new SafeValue(null, "member-not-found");
			}
			catch (Exception ex) { return new SafeValue(null, ExceptionName(ex)); }
		}

		private static PropertyInfo FindProperty(Type t, string name, BindingFlags flags)
		{
			for (Type x = t; x != null; x = x.BaseType)
			{
				try { PropertyInfo p = x.GetProperty(name, flags); if (p != null) return p; } catch { }
			}
			return null;
		}

		private static FieldInfo FindField(Type t, string name, BindingFlags flags)
		{
			for (Type x = t; x != null; x = x.BaseType)
			{
				try { FieldInfo f = x.GetField(name, flags); if (f != null) return f; } catch { }
			}
			return null;
		}

		private static Type FindType(string fullName)
		{
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				try { Type t = a.GetType(fullName, false); if (t != null) return t; } catch { }
			}
			return null;
		}

		private static object ReadFirst(object obj, params string[] names)
		{
			foreach (string n in names)
			{
				SafeValue v = ReadValue(obj, n);
				if (v.Error == null && v.Value != null) return v.Value;
			}
			return null;
		}

		private static bool Bool(object o) { return o is bool && (bool)o; }
		private static string SafeText(SafeValue v) { return v.Error != null ? "<ERROR:" + v.Error + ">" : SafeObjectText(v.Value); }
		private static string ArgsText(object[] a) { if (a == null) return "<null>"; return string.Join(", ", a.Select(SafeDescribe).ToArray()); }
		private static string ExceptionName(Exception ex) { while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException; return ex.GetType().Name + ": " + ex.Message; }
		private static string Flatten(Exception ex) { return ExceptionName(ex).Replace("\r", " ").Replace("\n", " "); }

		private static string ExceptionText(Exception ex)
		{
			StringBuilder sb = new StringBuilder();
			int depth = 0;
			for (Exception e = ex; e != null && depth < 16; e = e.InnerException, depth++)
			{
				sb.AppendLine("Exception[" + depth + "].Type: " + e.GetType().FullName);
				sb.AppendLine("Exception[" + depth + "].Message: " + e.Message);
				sb.AppendLine("Exception[" + depth + "].StackTrace:");
				sb.AppendLine(e.StackTrace ?? "<no stack trace>");
			}
			return sb.ToString();
		}

		private static string FormatMethod(MethodBase m)
		{
			if (m == null) return "<unknown method>";
			try { return (m.DeclaringType == null ? "<global>" : m.DeclaringType.FullName) + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")"; }
			catch { return m.Name; }
		}

		private static void AddBreadcrumb(string text)
		{
			try
			{
				string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + text;
				lock (Sync)
				{
					Breadcrumbs.Enqueue(line);
					while (Breadcrumbs.Count > MaxBreadcrumbs) Breadcrumbs.Dequeue();
				}
			}
			catch { }
		}

		private static string[] BreadcrumbSnapshot()
		{
			lock (Sync) return Breadcrumbs.ToArray();
		}

		public static string DumpBreadcrumbsToLog()
		{
			StringBuilder sb = NewSection("BREADCRUMBS");
			foreach (string b in BreadcrumbSnapshot()) sb.AppendLine(b);
			Append(sb.ToString());
			return "BREADCRUMBS_WRITTEN count=" + BreadcrumbSnapshot().Length + " log=" + LogPath;
		}

		public static string Status()
		{
			return "DIAG_V17 initialized=" + _initialized + ", patchedMethods=" + _patchedCount + ", lastContext=" + _lastContext + ", lastLink=" + _lastLink + ", log=" + LogPath;
		}

		private static void EnsureLogPath()
		{
			if (!string.IsNullOrEmpty(_logPath)) return;
			string root = ResolveGameRoot();
			string dir = Path.Combine(root, "Modules", "CharacterCrashGuard", "Logs");
			try { Directory.CreateDirectory(dir); }
			catch
			{
				dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord", "CharacterCrashGuard", "Logs");
				Directory.CreateDirectory(dir);
			}
			_logPath = Path.Combine(dir, "CharacterCrashGuard_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".log");
		}

		private static string ResolveGameRoot()
		{
			try
			{
				string loc = typeof(MBSubModuleBase).Assembly.Location;
				DirectoryInfo d = new DirectoryInfo(Path.GetDirectoryName(loc));
				if (d != null && d.Parent != null && d.Parent.Parent != null) return d.Parent.Parent.FullName;
			}
			catch { }
			try { return Directory.GetCurrentDirectory(); } catch { return "."; }
		}

		private static void WriteSessionHeader()
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("============================================================");
			sb.AppendLine("CHARACTER CRASH GUARD - DIAGNOSTIC REPORT");
			sb.AppendLine("============================================================");
			sb.AppendLine();
			sb.AppendLine("[SESSION]");
			sb.AppendLine("Started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
			sb.AppendLine("Character Crash Guard: " + ModVersion);
			sb.AppendLine("Compiled target: Bannerlord " + TargetGameVersion);
			sb.AppendLine("Runtime game version: " + RuntimeVersion());
			sb.AppendLine("Runtime: " + Environment.Version + " / " + Environment.OSVersion);
			sb.AppendLine("Process: " + Process.GetCurrentProcess().ProcessName + " pid=" + Process.GetCurrentProcess().Id);
			sb.AppendLine("Log: " + LogPath);
			sb.AppendLine();
			sb.AppendLine("[MODULES / ASSEMBLIES]");
			sb.AppendLine(ModuleAndAssemblyList());
			Append(sb.ToString());
		}

		private static string RuntimeVersion()
		{
			try
			{
				Type t = FindType("TaleWorlds.Core.MBSaveLoad");
				SafeValue current = ReadStaticValue(t, "CurrentVersion");
				SafeValue loaded = ReadStaticValue(t, "LastLoadedGameVersion");
				return "CurrentVersion=" + SafeText(current) + ", LastLoadedGameVersion=" + SafeText(loaded);
			}
			catch (Exception ex) { return "<ERROR: " + Flatten(ex) + ">"; }
		}

		private static string ModuleAndAssemblyList()
		{
			StringBuilder sb = new StringBuilder();
			try
			{
				Type mh = FindType("TaleWorlds.ModuleManager.ModuleHelper");
				MethodInfo gm = mh == null ? null : mh.GetMethods(SF).FirstOrDefault(m => m.Name == "GetModules" && m.GetParameters().Length <= 1);
				if (gm != null)
				{
					object modules = gm.Invoke(null, gm.GetParameters().Length == 0 ? null : new object[] { null });
					if (modules is IEnumerable)
					{
						sb.AppendLine("Modules discovered by ModuleHelper:");
						int n = 0;
						foreach (object m in (IEnumerable)modules)
						{
							if (m == null) continue;
							sb.AppendLine("  - " + SafeDescribe(m) + " Id=" + SafeText(ReadValue(m, "Id")) + " Version=" + SafeText(ReadValue(m, "Version")));
							if (++n > 200) break;
						}
					}
				}
			}
			catch (Exception ex) { sb.AppendLine("ModuleHelper listing failed: " + Flatten(ex)); }
			try
			{
				sb.AppendLine("Loaded managed assemblies:");
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
				{
					AssemblyName n = a.GetName();
					sb.AppendLine("  - " + n.Name + " " + n.Version);
				}
			}
			catch (Exception ex) { sb.AppendLine("Assembly listing failed: " + Flatten(ex)); }
			return sb.ToString();
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

		private static void WriteLine(string text) { Append("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + text); }

		private static void WriteDedup(string key, string text)
		{
			try
			{
				DateTime now = DateTime.UtcNow;
				lock (Sync)
				{
					DateTime last;
					if (RecentWarnings.TryGetValue(key, out last) && (now - last).TotalSeconds < 10) return;
					RecentWarnings[key] = now;
					if (RecentWarnings.Count > 500)
					{
						foreach (string k in RecentWarnings.Where(p => (now - p.Value).TotalMinutes > 5).Select(p => p.Key).Take(250).ToList()) RecentWarnings.Remove(k);
					}
				}
				Append(text);
			}
			catch { }
		}

		private static void EmergencyWrite(string text)
		{
			try
			{
				string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Mount and Blade II Bannerlord");
				Directory.CreateDirectory(d);
				File.AppendAllText(Path.Combine(d, "CharacterCrashGuard_emergency.log"), DateTime.Now.ToString("O") + " " + text + Environment.NewLine);
			}
			catch { }
		}

		private sealed class SafeValue
		{
			public readonly object Value;
			public readonly string Error;
			public SafeValue(object value, string error) { Value = value; Error = error; }
		}

		private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
		{
			public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
			public new bool Equals(object x, object y) => ReferenceEquals(x, y);
			public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
		}
	}
}
