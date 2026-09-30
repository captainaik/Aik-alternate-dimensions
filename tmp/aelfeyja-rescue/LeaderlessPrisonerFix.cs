using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AelfeyjaRescue
{
	/// <summary>
	/// Narrow safety fix for Bannerlord 1.5.3 HeroHelper.GetLastSeenText.
	/// Vanilla assumes that a mobile party holding a prisoner always has a LeaderHero.
	/// Bandit/deserter parties can legitimately be mobile, leaderless captors, causing
	/// a NullReferenceException when the encyclopedia tries to use LeaderHero.EncyclopediaLinkWithName.
	///
	/// This patch changes no campaign state. It only supplies a generic last-seen text
	/// for the proven unsafe case and lets every other call execute vanilla code.
	/// </summary>
	public sealed class LeaderlessPrisonerFixSubModule : MBSubModuleBase
	{
		protected override void OnSubModuleLoad()
		{
			base.OnSubModuleLoad();
			LeaderlessPrisonerFix.Initialize();
		}
	}

	public static class LeaderlessPrisonerFixCommands
	{
		[CommandLineFunctionality.CommandLineArgumentFunction("leaderless_prisoner_fix_status", "rescue")]
		public static string Status(List<string> args) => LeaderlessPrisonerFix.Status();
	}

	internal static class LeaderlessPrisonerFix
	{
		private const string Version = "v1.7.4";
		private const string HarmonyId = "aik.bannerlord.character_crash_guard.leaderless_prisoner_fix.v174";
		private static readonly BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
		private static readonly object Sync = new object();
		private static Harmony _harmony;
		private static bool _initialized;
		private static bool _patched;
		private static int _interventions;
		private static string _logPath;

		internal static void Initialize()
		{
			if (_initialized) return;
			_initialized = true;
			try
			{
				Type helperType = AccessTools.TypeByName("Helpers.HeroHelper");
				MethodInfo target = helperType?.GetMethod("GetLastSeenText", SF, null, new[] { typeof(Hero) }, null);
				if (target == null)
				{
					Write("INIT FAILED: Helpers.HeroHelper.GetLastSeenText(Hero) not found.");
					return;
				}

				_harmony = new Harmony(HarmonyId);
				_harmony.Patch(target, prefix: new HarmonyMethod(typeof(LeaderlessPrisonerFix).GetMethod(nameof(Prefix), SF)));
				_patched = true;
				Write("INIT OK version=" + Version + " target=Helpers.HeroHelper.GetLastSeenText(Hero) mode=narrow leaderless-prisoner fallback");
			}
			catch (Exception ex)
			{
				Write("INIT ERROR " + Flatten(ex));
			}
		}

		public static string Status()
		{
			return "LEADERLESS_PRISONER_FIX_V174 initialized=" + _initialized + " patched=" + _patched + " interventions=" + _interventions + " log=" + ResolveLogPath();
		}

		/// <summary>
		/// Return false only for the exact unsafe branch proven by runtime IL:
		/// prisoner + mobile captor + no army + null PartyBase.LeaderHero.
		/// </summary>
		public static bool Prefix(Hero __0, ref TextObject __result)
		{
			try
			{
				Hero hero = __0;
				if (hero == null || !hero.IsPrisoner)
					return true;

				PartyBase captor = hero.PartyBelongedToAsPrisoner;
				if (captor == null || !captor.IsMobile)
					return true;

				MobileParty mobile = captor.MobileParty;
				if (mobile == null)
					return true;

				// Vanilla has a separate army branch. We only fix the exact no-army path
				// that dereferences PartyBase.LeaderHero without a null check.
				if (mobile.Army != null || captor.LeaderHero != null)
					return true;

				var settlement = hero.LastKnownClosestSettlement;
				if (settlement == null)
					return true;

				TextObject text = GameTexts.FindText("str_last_seen_encyclopedia_entry");
				if (text == null)
					return true;

				text.SetTextVariable("IS_IN_SETTLEMENT", settlement == hero.CurrentSettlement ? 1 : 0);
				text.SetTextVariable("SETTLEMENT", settlement.EncyclopediaLinkWithName);
				__result = text;
				_interventions++;

				Write(
					"INTERCEPT hero=" + SafeHero(hero) +
					" captor=" + SafeMobile(mobile) +
					" settlement=" + SafeSettlement(settlement) +
					" reason=mobile captor has no Army and no LeaderHero; returned generic last-seen text without mutating campaign state");

				return false;
			}
			catch (Exception ex)
			{
				// Fail open: if our fix itself cannot prove it can safely produce a result,
				// let vanilla continue rather than hiding a different problem.
				Write("FIX ERROR (fail-open to vanilla): " + Flatten(ex));
				return true;
			}
		}

		private static string SafeHero(Hero hero)
		{
			if (hero == null) return "<null>";
			try { return hero.Name + "[" + hero.StringId + "]"; }
			catch { return "<unreadable hero>"; }
		}

		private static string SafeMobile(MobileParty party)
		{
			if (party == null) return "<null>";
			try { return party.Name + "[" + party.StringId + "]"; }
			catch { return "<unreadable mobile party>"; }
		}

		private static string SafeSettlement(TaleWorlds.CampaignSystem.Settlements.Settlement settlement)
		{
			if (settlement == null) return "<null>";
			try { return settlement.Name + "[" + settlement.StringId + "]"; }
			catch { return "<unreadable settlement>"; }
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
				_logPath = Path.Combine(logs, "CharacterCrashGuard_Fixes.log");
			}
			catch
			{
				_logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CharacterCrashGuard_Fixes.log");
			}
			return _logPath;
		}

		private static void Write(string text)
		{
			try
			{
				lock (Sync)
				{
					File.AppendAllText(ResolveLogPath(), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " " + text + Environment.NewLine, Encoding.UTF8);
				}
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
	}
}
