using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Guides;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Guides.Comps;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;

namespace HdtCompSorter;

/// <summary>
/// Re-sorts HDT's free Battlegrounds comp guide list (which HDT orders by name) using the
/// tier, tier rank and primary tribe that the free comp guides endpoint already returns:
/// comps playable in the current lobby first, then by tier, then by rank within the tier.
/// The Tier7 view (CompsByTier) is left untouched.
/// </summary>
public class CompSorterPlugin : IPlugin
{
	private static readonly TimeSpan RaceCheckInterval = TimeSpan.FromSeconds(1);

	private MethodInfo? _setComps;
	private bool _disabled;
	private List<BattlegroundsCompGuideViewModel>? _lastSorted;
	private string? _lastRacesKey;
	private DateTime _nextRaceCheck;

	public string Name => "BG Comp Sorter";
	public string Description => "Sorts the free Battlegrounds comp guides by lobby tribes, then tier and rank, instead of alphabetically.";
	public string ButtonText => "";
	public string Author => "Jesper";
	public Version Version => new(1, 0, 0);
	// HDT accepts plugins without a menu item
	public MenuItem MenuItem => null!;

	public void OnLoad()
	{
		_disabled = false;
		_lastSorted = null;
		_lastRacesKey = null;
		_setComps = typeof(BattlegroundsCompsGuidesViewModel)
			.GetProperty(nameof(BattlegroundsCompsGuidesViewModel.Comps))
			?.GetSetMethod(true);
		if(_setComps == null)
		{
			Log.Warn("[BG Comp Sorter] Comps setter not found, HDT internals changed. Plugin disabled.");
			_disabled = true;
		}
	}

	public void OnUnload()
	{
		_setComps = null;
		_lastSorted = null;
	}

	public void OnButtonPress()
	{
	}

	// HDT calls this on the UI thread roughly every 100ms
	public void OnUpdate()
	{
		if(_disabled)
			return;
		try
		{
			SortComps();
		}
		catch(Exception ex)
		{
			// stop rather than spam the log or interfere with HDT
			_disabled = true;
			Log.Error($"[BG Comp Sorter] Disabled after error: {ex}");
		}
	}

	private void SortComps()
	{
		var vm = Core.Overlay?.BattlegroundsCompsGuidesVM;
		var comps = vm?.Comps;
		if(vm == null || comps == null || comps.Count < 2 || vm.CompsByTier != null)
			return;

		var listChanged = !ReferenceEquals(comps, _lastSorted);

		// the lobby's tribes only exist in a match; GetAvailableRaces caches once known, but may read
		// game memory until then, so only poll it periodically while the list itself is unchanged
		HashSet<Race>? races = null;
		if(Core.Game.IsBattlegroundsMatch && !vm.IsPreLobby)
		{
			if(!listChanged && _lastRacesKey != null && DateTime.UtcNow < _nextRaceCheck)
				return;
			_nextRaceCheck = DateTime.UtcNow + RaceCheckInterval;
			races = BattlegroundsUtils.GetAvailableRaces();
		}

		var racesKey = races == null ? "" : string.Join(",", races.Select(r => (int)r).OrderBy(r => r));
		if(!listChanged && racesKey == _lastRacesKey)
			return;

		var sorted = comps
			.OrderBy(c => IsInLobby(c, races) ? 0 : 1)
			.ThenBy(c => c.CompGuide.Tier)
			.ThenBy(c => c.CompGuide.TierRank)
			.ThenBy(c => c.CompGuide.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToList();

		_lastRacesKey = racesKey;
		if(sorted.SequenceEqual(comps))
		{
			_lastSorted = comps;
			return;
		}

		_lastSorted = sorted;
		_setComps!.Invoke(vm, new object[] { sorted });
	}

	private static bool IsInLobby(BattlegroundsCompGuideViewModel comp, HashSet<Race>? races)
	{
		if(races == null)
			return true;
		var tribe = (Race)comp.PrimaryTribe;
		// tribeless comps (e.g. Menagerie) work in any lobby
		return tribe == Race.INVALID || tribe == Race.ALL || races.Contains(tribe);
	}
}
