using System;
using System.Linq;

using Noggog;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;

using TrueUnleveledSkyrim.Config;
using System.Collections.Immutable;

namespace TrueUnleveledSkyrim.Patch
{
    public class LeveledItemsPatcher
    {
        private static ArtifactKeys? artifactKeys;
        private static ExcludedLVLI? excludedLVLI;

        // Determines if a given leveled list holds artifacts or not based on the predefined EDID snippets in artifactKeys.json.
        private static bool IsArtifactList(LeveledItem itemList)
        {
            if (itemList.EditorID is null)
                return false;

            return artifactKeys!.Keys.Any(key => itemList.EditorID.Contains(key, StringComparison.OrdinalIgnoreCase));
        }

        // Checks if the given leveled list is an artifact list by checking if all the item names in the list are the same.
        private static bool IsArtifactList(LeveledItem itemList, ILinkCache linkCache)
        {
            // If defined in the artifactKeys.json list.
            if (IsArtifactList(itemList))
                return true;

            int entryCount = 0;
            string? itemName = null;
            foreach (LeveledItemEntry? itemEntry in itemList.Entries.EmptyIfNull())
            {
                if (itemEntry.Data is null)
                    return false;

                if (!itemEntry.Data.Reference.TryResolve(linkCache, out var resolvedItem))
                    return false;

                if (resolvedItem is not INamedGetter namedItem)
                    continue;

                if (itemName == namedItem.Name)
                {
                    ++entryCount;
                }
                else if (itemName is null)
                {
                    itemName = namedItem.Name;
                    ++entryCount;
                }
            }

            return entryCount == (itemList.Entries?.Count ?? 0);
        }

        // Removes every item from a list other than the highest level one.
        private static bool CullArtifactList(LeveledItem itemList)
        {
            if (itemList.Entries is null || !Patcher.ModSettings.Value.Items.UnlevelArtifacts)
                return false;

            bool wasChanged = false;
            var arrayData = itemList.Entries.Select(x => x.Data!.Level);
            int levelMax = arrayData.Any() ? arrayData.Max() : 0;
            for(int i=itemList.Entries.Count - 1; i>=0; --i)
            {
                if (itemList.Entries[i].Data!.Level != levelMax)
                {
                    itemList.Entries.RemoveAt(i);
                    wasChanged = true;
                }
            }

            return wasChanged;
        }

        // Checks if an item should be removed based on the minimum and maximum level set in the options.
        private static bool ShouldRemoveItem(ILeveledItemEntryDataGetter? itemData, ILinkCache linkCache)
        {
            if (itemData is null)
                return false;

            int maxLevel = Patcher.ModSettings.Value.Items.MaxItemLevel;
            int minLevel = Patcher.ModSettings.Value.Items.MinItemLevel;
            bool shouldRemove = itemData.Level > maxLevel && maxLevel != 0 || itemData.Level < minLevel;

            if(itemData.Level == maxLevel && !shouldRemove)
            {
                if(itemData.Reference.TryResolve(linkCache, out var resolvedItem) && resolvedItem.EditorID is not null)
                    shouldRemove = resolvedItem.EditorID.ToLower().Contains("glass");
            }

            return shouldRemove;
        }

        // Removes items from a list if they should be deleted.
        private static bool RemoveRareItems(LeveledItem itemList, ILinkCache linkCache)
        {
            bool wasChanged = false;
            for (int i = (itemList.Entries?.Count ?? 0) - 1; i >= 0; --i)
            {
                if (ShouldRemoveItem(itemList.Entries![i].Data, linkCache))
                {
                    itemList.Entries!.RemoveAt(i);
                    wasChanged = true;
                }
            }

            return wasChanged;
        }

        // Sets all the item levels in a list to 1.
        private static bool UnlevelList(LeveledItem itemList)
        {
            bool wasChanged = false;
            foreach (var entry in itemList.Entries.EmptyIfNull())
            {
                if (entry.Data == null || entry.Data.Level == 1) continue;

                entry.Data.Level = 1;
                wasChanged = true;
            }

            return wasChanged;
        }

        // Gets the lowest and highest levels in a list.
        private static void GetLevelBoundaries(LeveledItem itemList, out int lvlMin, out int lvlMax)
        {
            lvlMin = Int16.MaxValue; lvlMax = -1;
            foreach (var entry in itemList.Entries.EmptyIfNull())
            {
                if (entry.Data is null) continue;
                if (entry.Data.Level < lvlMin) lvlMin = entry.Data.Level;
                if (entry.Data.Level > lvlMax) lvlMax = entry.Data.Level;
            }
        }

        // Removes items in a given level range from a list.
        private static bool RemoveItemsWithRange(LeveledItem itemList, int rangeLow, int rangeHigh)
        {
            bool wasChanged = false;
            for (int i = (itemList.Entries?.Count ?? 0) - 1; i >= 0; --i)
            {
                if (itemList.Entries![i].Data is null) continue;

                if (itemList.Entries![i].Data!.Level >= rangeLow && itemList.Entries![i].Data!.Level <= rangeHigh)
                {
                    itemList.Entries!.RemoveAt(i);
                    wasChanged = true;
                }
            }

            return wasChanged;
        }

        private static void ChangeNewLVLIEntries(LeveledItem itemList, IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ILinkCache linkCache)
        {
            bool isWeakEntry = itemList.EditorID!.Contains(TUSConstants.WeakPostfix, StringComparison.OrdinalIgnoreCase);
            bool isStrongEntry = !isWeakEntry && itemList.EditorID!.Contains(TUSConstants.StrongPostfix, StringComparison.OrdinalIgnoreCase);
            if (!isWeakEntry && !isStrongEntry)
                return;

            string usedPostfix = isWeakEntry ? TUSConstants.WeakPostfix : TUSConstants.StrongPostfix;
            foreach(LeveledItemEntry entry in itemList.Entries.EmptyIfNull())
            {
                if (entry.Data is null) continue;
                IItemGetter? resolvedEntry = entry.Data.Reference.TryResolve(linkCache);
                if(resolvedEntry is ILeveledItemGetter lvliGetter)
                {
                    LeveledItem? newEntry = state.PatchMod.LeveledItems.Where(x => x.EditorID == lvliGetter.EditorID + usedPostfix).FirstOrDefault();
                    if(newEntry is not null)
                        entry.Data.Reference = newEntry.ToLink();
                }
            }
        }

        // Main function to unlevel all leveled item lists.
        public static void PatchLVLI(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            artifactKeys = JsonHelper.LoadConfig<ArtifactKeys>(TUSConstants.ArtifactKeysPath);
            excludedLVLI = JsonHelper.LoadConfig<ExcludedLVLI>(TUSConstants.ExcludedLVLIPath);
            bool allowEmptyLists = Patcher.ModSettings.Value.Items.AllowEmptyLists;

            uint processedRecords = 0;
            var staticList = state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides().ToImmutableList();
            foreach (var lvlItemGetter in staticList)
            {
                bool wasChanged = false;
                LeveledItem listCopy = lvlItemGetter.DeepCopy();

                if (IsArtifactList(listCopy, Patcher.LinkCache))
                {
                    wasChanged |= CullArtifactList(listCopy);
                }
                else
                {
                    if (listCopy.EditorID is not null &&
                        excludedLVLI.Keys.Any(key => listCopy.EditorID.Contains(key, StringComparison.OrdinalIgnoreCase)) && !excludedLVLI.ForbiddenKeys.Any(key => listCopy.EditorID.Contains(key, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    wasChanged |= RemoveRareItems(listCopy, Patcher.LinkCache);
                    GetLevelBoundaries(listCopy, out var lvlMin, out var lvlMax);
                    if (lvlMin != short.MaxValue && lvlMax != -1 && lvlMin != lvlMax)
                    {
                        var weakCopy = new LeveledItem(state.PatchMod);
                        var strongCopy = new LeveledItem(state.PatchMod);
                        weakCopy.DeepCopyIn(listCopy);
                        strongCopy.DeepCopyIn(listCopy);
                        weakCopy.EditorID += TUSConstants.WeakPostfix;
                        strongCopy.EditorID += TUSConstants.StrongPostfix;

                        int lvlMed = (int)Math.Round((lvlMin + lvlMax) * 0.465);
                        RemoveItemsWithRange(weakCopy, lvlMed + 1, lvlMax);
                        RemoveItemsWithRange(strongCopy, lvlMin, lvlMed - 1);

                        UnlevelList(weakCopy);
                        UnlevelList(strongCopy);

                        if (weakCopy.Entries is not null && weakCopy.Entries.Any())
                            state.PatchMod.LeveledItems.Set(weakCopy);
                        if (strongCopy.Entries is not null && strongCopy.Entries.Any())
                            state.PatchMod.LeveledItems.Set(strongCopy);
                    }
                }

                if (!allowEmptyLists && (listCopy.Entries is null || !listCopy.Entries.Any()))
                {
                    listCopy.DeepCopyIn(lvlItemGetter);
                    wasChanged = false;
                }

                wasChanged |= UnlevelList(listCopy);

                ++processedRecords;
                if (processedRecords % 100 == 0)
                    Console.WriteLine("Processed " + processedRecords + " leveled item lists.");

                if (wasChanged)
                    state.PatchMod.LeveledItems.Set(listCopy);
            }

            Console.WriteLine("Updating newly generated leveled list references.");
            foreach(var entry in state.PatchMod.LeveledItems)
                ChangeNewLVLIEntries(entry, state, Patcher.LinkCache);

            Console.WriteLine("Processed " + processedRecords + " leveled item lists in total.\n");
        }
    }
}
