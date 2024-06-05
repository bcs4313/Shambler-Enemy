using System;
using System.Collections.Generic;
using System.Diagnostics;

// Reads the config spawn distribution and returns a spawn multiplier accordingly
namespace MoaiEnemy.src.Utilities
{
    public class RawspawnHandler
    {
        public struct enemyRarityPkg
        {
            public string name;
            public int rarity;
        }

        // Updates all spawn multiplier values for the current level
        // It does this by manually looking into the spawntable and setting rarities there
        public static void setLevelSpawnWeights(enemyRarityPkg[] dayReferences, enemyRarityPkg[] nightReferences)
        {
            RoundManager rm = RoundManager.Instance;
            var level = rm.currentLevel;

            // day loop
            for (int i = 0; i < level.DaytimeEnemies.Count; i++)
            {
                var enemy = level.DaytimeEnemies[i];

                foreach (enemyRarityPkg moai in dayReferences)
                {
                    if (enemy.enemyType.name.Equals(moai.name))
                    {
                        level.DaytimeEnemies[i].rarity = moai.rarity;
                    }
                }
            }

            // night loop
            for (int i = 0; i < level.OutsideEnemies.Count; i++)
            {
                var enemy = level.OutsideEnemies[i];

                foreach (enemyRarityPkg moai in nightReferences)
                {
                    if (enemy.enemyType.name.Equals(moai.name))
                    {
                        level.OutsideEnemies[i].rarity = moai.rarity;
                    }
                }
            }
        }

        // Reads the config spawn distribution and returns a spawn multiplier accordingly
        public static float getSpawnMultiplier(bool pickMax = false)
        {
            float finalMultiplier = 0;
            float randomRoll = UnityEngine.Random.Range(0f, 1f);

            // parallel lists
            List<float> chances = new List<float>();
            List<float> multipliers = new List<float>();

            string distribution = Plugin.moaiSpawnDistribution.Value;
            string[] distribution_entries = distribution.Split(",");

            try
            {
                foreach (string entry in distribution_entries)
                {
                    string[] values = entry.Split("%");
                    chances.Add(float.Parse(values[0]) / 100f);
                    multipliers.Add(float.Parse(values[1]) / 100f);
                }

                if (pickMax)
                {
                    float largestMult = 0f;
                    for(int i = 0; i < multipliers.Count; i++)
                    {
                        float multiplier = multipliers[i];
                        if (multiplier > largestMult)
                        {
                            largestMult = multiplier;
                            finalMultiplier = multiplier;
                        }
                    }
                }
                else
                {

                    // now to actually run the distribution chance
                    // use a accum variable to represent the "gap" in chance for each multiplier
                    float accum = 0;
                    for (int i = 0; i < chances.Count; i++)
                    {
                        float chance = chances[i];
                        float multiplier = multipliers[i];
                        if (randomRoll > accum && randomRoll < chance + accum)
                        {
                            finalMultiplier = multiplier;
                            break;
                        }
                        accum += chance;
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("Moai Enemy: Exception when parsing spawn distribution chances! Error is: " + e.ToString());
            }

            UnityEngine.Debug.Log("Moai Enemy: Spawn Distribution Parsed is -->");
            if (pickMax)
            {
                UnityEngine.Debug.Log("Moai Enemy: Picked largest spawn multiplier due to being in easter island.");
                UnityEngine.Debug.Log("Moai Enemy: Multipliers:");
                UnityEngine.Debug.Log(string.Join(",", multipliers));
                UnityEngine.Debug.Log("Moai Enemy: Selected multiplier for the day is " + finalMultiplier * 100 + "%");
            }
            else
            {
                UnityEngine.Debug.Log("Moai Enemy: Chances:");
                UnityEngine.Debug.Log(string.Join(",", chances));
                UnityEngine.Debug.Log("Moai Enemy: Multipliers:");
                UnityEngine.Debug.Log(string.Join(",", multipliers));
                UnityEngine.Debug.Log("Moai Enemy: Random chance roll:");
                UnityEngine.Debug.Log(randomRoll);
                UnityEngine.Debug.Log("Moai Enemy: Selected multiplier for the day is " + finalMultiplier * 100 + "%");
            }

            return finalMultiplier;
        }
    }
}
