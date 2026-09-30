using System;
using TaleWorlds.CampaignSystem.Encyclopedia;
using TaleWorlds.CampaignSystem.ViewModelCollection;

namespace AelfeyjaRescue
{
	// Never invoked at runtime. This exists so CI compilation against the exact
	// Bannerlord 1.5.3 reference assemblies fails if the primary diagnostic API
	// contract changes: HeroVM.ExecuteLink and both EncyclopediaManager.GoToLink overloads.
	internal static class ApiContractCheck
	{
		internal static void Verify(HeroVM heroVm, EncyclopediaManager manager)
		{
			Action heroExecute = heroVm.ExecuteLink;
			Action<string> goToLink = manager.GoToLink;
			Action<string, string> goToTypedLink = manager.GoToLink;
			GC.KeepAlive(heroExecute);
			GC.KeepAlive(goToLink);
			GC.KeepAlive(goToTypedLink);
		}
	}
}
