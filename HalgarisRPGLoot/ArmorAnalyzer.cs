﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.FormKeys.SkyrimSE;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;
using Noggog;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HalgarisRPGLoot
{
    public class ArmorAnalyzer
    {
        private Settings Settings = new Settings();

        public IPatcherState<ISkyrimMod, ISkyrimModGetter> State { get; set; }
        public ILeveledItemGetter[] AllLeveledLists { get; set; }
        public ResolvedListItem<IArmorGetter>[] AllListItems { get; set; }
        public ResolvedListItem<IArmorGetter>[] AllEnchantedItems { get; set; }
        public ResolvedListItem<IArmorGetter>[] AllUnenchantedItems { get; set; }


        public Dictionary<int, ResolvedEnchantment[]> ByLevelIndexed { get; set; }

        public Dictionary<FormKey, IObjectEffectGetter> AllObjectEffects { get; set; }


        public ResolvedEnchantment[] AllEnchantments { get; set; }
        public HashSet<short> AllLevels { get; set; }

        public (short Key, ResolvedEnchantment[])[] ByLevel { get; set; }
      
        
        public ArmorAnalyzer(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)

        {
            State = state;
            LoadSettings();
        }

        private void LoadSettings()
        {
            const string path = "Data/ArmorSettings.json";
            if (!File.Exists(path))
            {
                // Ensure the default settings are saved
                Settings.InitializeDefault();
                SaveSettings();
                return;
            }
            using (var file = File.OpenText(path))
            {
                var serializer = new JsonSerializer();
                Settings = (Settings)serializer.Deserialize(file, typeof(Settings));
            }
        }

        private void SaveSettings()
        {
            const string path = "Data/ArmorSettings.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var file = File.CreateText(path))
            {
                var serializer = new JsonSerializer();
                serializer.Serialize(file, Settings);
            }
        }

        public void Analyze()
        {
            AllLeveledLists = State.LoadOrder.PriorityOrder.WinningOverrides<ILeveledItemGetter>().ToArray();

            AllListItems = AllLeveledLists.SelectMany(lst => lst.Entries?.Select(entry =>
                                                             {
                                                                 if (entry?.Data?.Reference.FormKey == default) return default;
                    
                                                                 if (!State.LinkCache.TryResolve<IArmorGetter>(entry.Data.Reference.FormKey,

                                                                     out var resolved))
                                                                     return default;
                                                                 return new ResolvedListItem<IArmorGetter>
                                                                 {
                                                                     List = lst,
                                                                     Entry = entry,
                                                                     Resolved = resolved
                                                                 };
                                                             }).Where(r => r != default)
                                                             ?? new ResolvedListItem<IArmorGetter>[0])
                .Where(e =>
                {
                    var kws = (e.Resolved.Keywords ?? new IFormLink<IKeywordGetter>[0]);
                    return !kws.Contains(Skyrim.Keyword.MagicDisallowEnchanting);
                })
                .ToArray();

            AllUnenchantedItems = AllListItems.Where(e => e.Resolved.ObjectEffect.IsNull).ToArray();

            AllEnchantedItems = AllListItems.Where(e => !e.Resolved.ObjectEffect.IsNull).ToArray();

            AllObjectEffects = State.LoadOrder.PriorityOrder.ObjectEffect().WinningOverrides()
                .ToDictionary(k => k.FormKey);

            AllEnchantments = AllEnchantedItems
                .Select(e => (e.Entry.Data.Level, e.Resolved.EnchantmentAmount, e.Resolved.ObjectEffect.FormKey))
                .Distinct()
                .Select(e =>
                {
                    if (!AllObjectEffects.TryGetValue(e.FormKey, out var ench))
                        return default;
                    return new ResolvedEnchantment
                    {
                        Level = e.Level,
                        Amount = e.Item2,
                        Enchantment = ench
                    };
                })
                .Where(e => e != default)
                .ToArray();

            AllLevels = AllEnchantments.Select(e => e.Level).Distinct().ToHashSet();


            ByLevel = AllEnchantments.GroupBy(e => e.Level)
                .OrderBy(e => e.Key)
                .Select(e => (e.Key, e.ToArray()))
                .ToArray();

            ByLevelIndexed = Enumerable.Range(0, 100)
                .Select(lvl => (lvl, ByLevel.Where(bl => bl.Key <= lvl).SelectMany(e => e.Item2).ToArray()))
                .ToDictionary(kv => kv.lvl, kv => kv.Item2);
        }



        public void Report()
        {
            Console.WriteLine($"Found: {AllLeveledLists.Length} leveled lists");
            Console.WriteLine($"Found: {AllListItems.Length} items");
            Console.WriteLine($"Found: {AllUnenchantedItems.Length} un-enchanted items");
            Console.WriteLine($"Found: {AllEnchantedItems.Length} enchanted items");
        }

        public void Generate()
        {
            var rand = new Random(Guid.NewGuid().GetHashCode());

            foreach (var ench in AllUnenchantedItems)
            {
                var lst = State.PatchMod.LeveledItems.AddNew(State.PatchMod.GetNextFormKey());
                lst.DeepCopyIn(ench.List);
                lst.EditorID = "HAL_TOP_LList" + ench.Resolved.EditorID;
                lst.Entries!.Clear();
                lst.Flags &= ~LeveledItem.Flag.UseAll;

                // Roll EnchantmentsPer times
                // use value to assign which Rarities we create
                var numEntriesPerRarity = GenerateRarityEntryCounts(rand);

                for (int i = 0; i < Settings.Rarities.Count(); i++)
                {
                    if (numEntriesPerRarity[i] == 0)
                    {
                        // Skip empty rarities...otherwise they will strip our citizens.
                        continue;
                    }
                    var e = Settings.Rarities[i];
                    var numEntries = numEntriesPerRarity[i];
                    var nlst = State.PatchMod.LeveledItems.AddNew(State.PatchMod.GetNextFormKey());
                    nlst.DeepCopyIn(ench.List);
                    nlst.EditorID = "HAL_LList_" + e.Label + "_" + ench.Resolved.EditorID;
                    nlst.Entries!.Clear();
                    nlst.Flags &= ~LeveledItem.Flag.UseAll;

                    for (var j = 0; j < numEntries; j++)
                    {
                        var itm = GenerateEnchantment(ench, e.Label, e.NumEnchantments);
                        var entry = ench.Entry.DeepCopy();
                        entry.Data!.Reference.SetTo(itm);
                        nlst.Entries.Add(entry);
                    }

                    for (var j = 0; j < e.LLEntries; j++)
                    {
                        var lentry = ench.Entry.DeepCopy();
                        lentry.Data!.Reference.SetTo(nlst);
                        lst.Entries.Add(lentry);
                    }
                }

                var remain = 240 - Settings.Rarities.Sum(e => e.LLEntries);
                for (var i = 0; i < remain; i++)
                {
                    var lentry = ench.Entry.DeepCopy();
                    lentry.Data!.Reference.SetTo(ench.Resolved);
                    lst.Entries.Add(lentry);
                }

                var olst = State.PatchMod.LeveledItems.GetOrAddAsOverride(ench.List);
                foreach (var entry in olst.Entries!.Where(entry =>
                    entry.Data!.Reference.FormKey == ench.Resolved.FormKey))
                {
                    entry.Data!.Reference.SetTo(lst);
                }
            }
        }

        private int[] GenerateRarityEntryCounts(Random rand)
        {
            var rarityWeight = (float)Settings.Rarities.Sum(r => r.LLEntries);
            foreach(var rarity in Settings.Rarities)
            {
                Console.WriteLine($"{rarity.Label}: Entries - {rarity.LLEntries} Enchantments - {rarity.NumEnchantments}");
            }
            Console.WriteLine($"RarityWeight: {rarityWeight}");
            var spawnChances = Settings.Rarities.Select(r => (float)r.LLEntries).ToList();

            var counts = new int[spawnChances.Count()];
            if (Settings.UseRNGRarities)
            {
                // Precalculate the maximum number in our rolls for each range
                for (int i = 0; i < spawnChances.Count(); i++)
                {
                    for (int j = 0; j < i; j++)
                    {
                        spawnChances[i] += spawnChances[j];
                    }
                }
                for (int i = 0; i < Settings.VarietyCountPerItem; i++)
                {
                    var v = rand.Next() % rarityWeight;
                    for (int j = spawnChances.Count() - 1; j >= 0; j--)
                    {
                        if (v <= spawnChances[j] && (j == 0 || v > spawnChances[j-1]))
                        {
                            counts[j]++;
                            break;
                        }
                    }
                }
            }
            else
            {
                for (int j = spawnChances.Count() - 1; j >= 0; j--)
                {
                    counts[j] = (int)((spawnChances[j] / rarityWeight) * Settings.VarietyCountPerItem);
                }
            }

            return counts;
        }

        private FormKey GenerateEnchantment(
            ResolvedListItem<IArmorGetter> item,
            string rarityName, int rarityEnchCount)
        {
            var level = item.Entry.Data.Level;
            var forLevel = ByLevelIndexed[level];
            var effects = Extensions.Repeatedly(() => forLevel.RandomItem())
                .Distinct()
                .Take(rarityEnchCount)
                .Shuffle();

            var oldench = effects.First().Enchantment;
            var key = State.PatchMod.GetNextFormKey();
            var nrec = State.PatchMod.ObjectEffects.AddNew(key);
            nrec.DeepCopyIn(effects.First().Enchantment);
            nrec.EditorID = "HAL_ARMOR_ENCH_" + oldench.EditorID;
            nrec.Name = rarityName + " " + oldench.Name;
            nrec.Effects.Clear();
            nrec.Effects.AddRange(effects.SelectMany(e => e.Enchantment.Effects).Select(e => e.DeepCopy()));
            nrec.WornRestrictions.SetTo(effects.First().Enchantment.WornRestrictions);

            string itemName = "";
            if (!(item.Resolved?.Name?.TryLookup(Mutagen.Bethesda.Strings.Language.English, out itemName) ?? false))
            {
                itemName = MakeName(item.Resolved.EditorID);

            }
            var nitm = State.PatchMod.Armors.AddNewLocking(State.PatchMod.GetNextFormKey());
            nitm.DeepCopyIn(item.Resolved);
            nitm.EditorID = "HAL_ARMOR_" + nitm.EditorID;
            nitm.ObjectEffect.SetTo(nrec);
            nitm.Name = rarityName + " " + itemName + " of " + effects.First().Enchantment.Name;


                return nitm.FormKey;
            }
            catch (Exception ex)
            {
                throw RecordException.Factory(ex, item.Resolved);
            }
        }

        private static char[] Numbers = "123456890".ToCharArray();
        private static Regex Splitter = new Regex("(?<=[A-Z])(?=[A-Z][a-z])|(?<=[^A-Z])(?=[A-Z])|(?<=[A-Za-z])(?=[^A-Za-z])");
        private Dictionary<string, string> KnownMapping = new Dictionary<string, string>();
        private string MakeName(string? resolvedEditorId)
        {
            string returning;
            if (resolvedEditorId == null)
            {
                returning = "Armor";
            }
            else
            {
                if (KnownMapping.TryGetValue(resolvedEditorId, out var cached))
                    return cached;

                var parts = Splitter.Split(resolvedEditorId)
                    .Where(e => e.Length > 1)
                    .Where(e => e != "DLC" && e != "Armor" && e != "Variant")
                    .Where(e => !int.TryParse(e, out var _))
                    .ToArray();
                if (parts.First() == "Clothes" && parts.Last() == "Clothes")
                    parts = parts.Skip(1).ToArray();
                if (parts.Length >= 2 && parts.First() == "Clothes")
                    parts = parts.Skip(1).ToArray();
                returning = string.Join(" ", parts);
                KnownMapping[resolvedEditorId] = returning;
            }
            Console.WriteLine($"Missing armor name for {resolvedEditorId ?? "<null>"} using {returning}");

            return returning;
        }
    }
}