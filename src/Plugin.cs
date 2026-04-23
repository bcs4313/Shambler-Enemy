using System.Reflection;
using UnityEngine;
using BepInEx;
using HarmonyLib;
using LethalLib.Modules;
using static LethalLib.Modules.Levels;
using static LethalLib.Modules.Enemies;
using BepInEx.Logging;
using System.IO;
using BepInEx.Configuration;
using LethalConfig.ConfigItems;
using LethalConfig.ConfigItems.Options;
using LethalConfig;
using System.Collections.Generic;
using System;
using SolidLib.Registry;
using System.Text.RegularExpressions;
using SoulDev;
using Shambler.src.Soul_Devourer;
using System.Threading.Tasks;
using GameNetcodeStuff;
using static UnityEngine.ParticleSystem.PlaybackState;

namespace Shambler
{
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    [BepInDependency(SolidLib.PluginInformation.PLUGIN_GUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony _harmony;
        public static new ManualLogSource Logger;

        public static float rawSpawnMultiplier = 0f;

        public static GameObject ShamblerStakePrefab;

        public static void LogDebug(string text)
        {
#if DEBUG
            Plugin.Logger.LogInfo(text);
#endif
        }

        public static void LogProduction(string text)
        {
            Plugin.Logger.LogInfo(text);
        }

        private void Awake()
        {
            Logger = base.Logger;
            bindVars();

            // Required by https://github.com/EvaisaDev/UnityNetcodePatcher
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types)
            {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods)
                {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0)
                    {
                        method.Invoke(null, null);
                    }
                }
            }

            UnityEngine.Random.InitState((int)System.DateTime.Now.Ticks);

            try
            {
                String targetAssetName = "Shambler";

                /*
                if(daytimeSpawn.Value)
                {
                    targetAssetName = "SoulDevDaytime";
                }
                */

                String trueDistribution = ScaleAllIntegers(Plugin.devourerSpawnDist.Value, Plugin.soulRarity.Value);

                // SolidLib enemy Registering
                List<EnemyConfig> list = new List<EnemyConfig>();
                list.Add(new EnemyConfig
                {
                    Name = "Shambler Enemy",
                    AssetName = targetAssetName,
                    TerminalKeywordAsset = "ShamblerTK",
                    TerminalNodeAsset = "ShamblerTN",
                    Enabled = true,
                    MaxSpawnCount = Plugin.maxCount.Value,
                    PowerLevel = 2f,
                    SpawnWeights = trueDistribution,
                });
                EnemyInitializer.Initialize("bcs_shamblerenemybundle", list);

                // part that loads the Shambler's spike
                Assets.PopulateAssets();
                ShamblerStakePrefab = Assets.MainAssetBundle.LoadAsset<GameObject>("ShamblerStake");
                NetworkPrefabs.RegisterNetworkPrefab(ShamblerStakePrefab);

                Debug.Log("Shambler Enemy loaded with the following spawn weights: " + trueDistribution);
            }
            catch (Exception e)
            {
                Debug.LogError("Error initializing the Shambler Enemy, maybe the spawn distribution you entered is malformed? : error -> " + e.ToString());
            }

            // flush out static values for the shambler each landing, prevents persistent bugs
            On.RoundManager.SpawnScrapInLevel += (On.RoundManager.orig_SpawnScrapInLevel orig, global::RoundManager self) =>
            {
                orig.Invoke(self);
                try
                {
                    ShamblerEnemy.stuckPlayerIds.Clear();

                }
                catch(Exception e)
                {
                    Debug.LogError("Shambler static value reset error: " + e.ToString());
                }
            };

            // Stake despawning
            On.RoundManager.DespawnPropsAtEndOfRound += (On.RoundManager.orig_DespawnPropsAtEndOfRound orig, global::RoundManager self, bool despawnAllItems) =>
            {
                orig.Invoke(self, despawnAllItems);
                if (RoundManager.Instance.IsHost)
                {
                    try
                    {
                        foreach (ShamblerStake stake in FindObjectsOfType<ShamblerStake>())
                        {
                            if (stake.gameObject)
                            {
                                Destroy(stake.gameObject);
                            }
                        }

                    }
                    catch (Exception e)
                    {
                        Debug.LogError("Shambler static value reset error: " + e.ToString());
                    }
                }
                else
                {
                    // gives time for the ro
                    LateCleanupClient();
                }
            };

            // yet another patch to stop the client from taking fall damage...
            On.GameNetcodeStuff.PlayerControllerB.DamagePlayer += (On.GameNetcodeStuff.PlayerControllerB.orig_DamagePlayer orig,
                global::GameNetcodeStuff.PlayerControllerB self,
                int damageNumber, bool hasDamageSFX, bool callRPC, CauseOfDeath causeOfDeath, int deathAnimation, bool fallDamage, Vector3 force) =>
            {
                try
                {
                    // the method is invoked normally EXCEPT in scenarios where the player is taking fall damage while attached to a shambler
                    if (fallDamage == true)
                    {
                        // conditional
                        if (!PlayerAttachedToShamblerOrStake(self))
                        {
                            // take normal dmg
                            orig.Invoke(self, damageNumber, hasDamageSFX, callRPC, causeOfDeath, deathAnimation, fallDamage, force);
                        }
                        else
                        {
                            Debug.Log("Shambler FallDmg Cancel: = 0");
                            // take hit, but dmg is 0
                            orig.Invoke(self, 0, hasDamageSFX, callRPC, causeOfDeath, deathAnimation, fallDamage, force);
                        }
                    }
                    else
                    {  // take dmg normally
                        orig.Invoke(self, damageNumber, hasDamageSFX, callRPC, causeOfDeath, deathAnimation, fallDamage, force);
                    }
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                orig.Invoke(self, damageNumber, hasDamageSFX, callRPC, causeOfDeath, deathAnimation, fallDamage, force);
            };

            On.GameNetcodeStuff.PlayerControllerB.KillPlayer += (On.GameNetcodeStuff.PlayerControllerB.orig_KillPlayer orig, global::GameNetcodeStuff.PlayerControllerB self, Vector3 bodyVelocity, bool spawnBody, CauseOfDeath causeOfDeath, int deathAnimation, Vector3 positionOffset) =>
            {
                try
                {
                    // the method is invoked normally EXCEPT in scenarios where the player is taking fall damage while attached to a shambler
                    if (causeOfDeath == CauseOfDeath.Gravity)
                    {
                        // conditional
                        if (!PlayerAttachedToShamblerOrStake(self))
                        {
                            // kill normally
                            orig.Invoke(self, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset);
                        }
                        else
                        {
                            Debug.Log("Shambler FallDmgKill Cancel: = 0");
                            // do NOT hit at all
                        }
                    }
                    else
                    {
                        // kill normally
                        orig.Invoke(self, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset);
                    }
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                orig.Invoke(self, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset);
            };

            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }


        public bool PlayerAttachedToShamblerOrStake(PlayerControllerB ply)
        {
            var shamblers = FindObjectsOfType<ShamblerEnemy>();
            foreach(var shambler in shamblers)
            {
                if(shambler.capturedPlayer && shambler.capturedPlayer.NetworkObject.NetworkObjectId == ply.NetworkObject.NetworkObjectId)
                {
                    return true;
                }

                if (shambler.stabbedPlayer && shambler.stabbedPlayer.NetworkObject.NetworkObjectId == ply.NetworkObject.NetworkObjectId)
                {
                    return true;
                }
            }

            var stakes = FindObjectsOfType<ShamblerStake>();
            foreach(var stake in stakes)
            {
                if(stake.victim && stake.victim.NetworkObject.NetworkObjectId == ply.NetworkObject.NetworkObjectId)
                {
                    return true;
                }
            }
            return false;
        }

        public async void LateCleanupClient()
        {
            await Task.Delay(5000);
            foreach (ShamblerStake stake in FindObjectsOfType<ShamblerStake>())
            {
                if (stake.gameObject)
                {
                    Destroy(stake.gameObject);
                }
            }
        }

        public static class Assets
        {
            public static AssetBundle MainAssetBundle = null;
            public static void PopulateAssets()
            {
                string sAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                MainAssetBundle = AssetBundle.LoadFromFile(Path.Combine(sAssemblyLocation, "bcs_shamblerstake"));

                if (MainAssetBundle == null)
                {
                    Plugin.Logger.LogError("Failed to load custom assets.");
                    return;
                }
            }
        }

        // quickly scale the spawn distribution by the base spawnrate of the enemy
        public static string ScaleAllIntegers(string input, float multiplier)
        {
            return Regex.Replace(input, @"\d+", match =>
            {
                int original = int.Parse(match.Value);
                int scaled = (int)Math.Round(original * multiplier);
                return scaled.ToString();
            });
        }

        // SETTINGS SECTION
        // consider these multipliers for existing values
        public static ConfigEntry<float> moaiGlobalMusicVol;
        public static ConfigEntry<float> moaiGlobalSpeed;
        public static ConfigEntry<float> soulRarity;
        public static ConfigEntry<String> devourerSpawnDist;
        public static ConfigEntry<bool> spawnsOutside;
        public static ConfigEntry<float> LOSWidth;
        public static ConfigEntry<int> maxCount;
        public static ConfigEntry<int> health;
        public static ConfigEntry<bool> canEnterIndoors;
        public static ConfigEntry<bool> disableColliderOnDeath;

        // behavior related
        public static ConfigEntry<float> shipSafeZoneRadius;
        public static ConfigEntry<int> stakeFreeChance;
        public static ConfigEntry<int> stakeFailDmg;

        public void bindVars()
        {
            soulRarity = Config.Bind("Spawning", "Enemy Spawnrate Multiplier", 1f, "Changes the spawnrate of the Shambler across ALL planets. Decimals are accepted, 2.0 = approximately double the spawnrate. 0.5 is half the spawnrate.");
            devourerSpawnDist = Config.Bind("Spawning", "Enemy Spawn Weights", "ExperimentationLevel:8,AssuranceLevel:24,OffenseLevel:24,MarchLevel:40,AdamanceLevel:40,DineLevel:17,RendLevel:17,TitanLevel:14,ArtificeLevel:20,Modded:24", "The spawn weight of the Shambler (multiplied by enemy spawnrate value) and the moons that the enemy can spawn on, in the form of a comma separated list of selectable level names and a weight value (e.g. \"ExperimentationLevel:300,DineLevel:20,RendLevel:10,Modded:10\")\r\nThe following strings: \"All\", \"Vanilla\", \"Modded\" are also valid.");
            maxCount = Config.Bind("Spawning", "Enemy Max Count", 3, "The maximum amount of Shamblers that can spawn in one day (hard cap).");
            moaiGlobalMusicVol = Config.Bind("Modifiers", "Enemy Sound Volume", 0.6f, "Changes the volume of all Shambler sounds. May make them more sneaky as well.");
            //moaiGlobalSizeVar = Config.Bind("Modifiers", "Size Variant Chance", 0.2f, "The chance of a soul devourer to spawn in a randomly scaled size. Affects their pitch too.");
            //moaiGlobalSize = Config.Bind("Modifiers", "Size Multiplier", 1f, "Changes the size of all soul devourer models. Scales pretty violently. Affects SFX pitch.");
            //moaiSizeCap = Config.Bind("Modifiers", "Size Variant Cap", 100f, "Caps the max size of a soul devourer with the size variant. Normal size is 1. 1.5 is slightly taller than the ship. 2 is very large. 3.5+ is giant tier (with 5 being the largest usually)");
            moaiGlobalSpeed = Config.Bind("Modifiers", "Enemy Speed Multiplier", 1f, "Changes the speed of all Shamblers. 4x would mean they are 4 times faster, 0.5x would be 2 times slower.");
            health = Config.Bind("Modifiers", "Enemy Health", 6, "Changes the health of all shamblers.");
            //daytimeSpawn = Config.Bind("Spawning", "Can spawn at daytime", false, "Can the enemy spawn in the daytime? Good luck if this is on.");
            LOSWidth = Config.Bind("Advanced", "Line Of Sight Width", 100f, "Line of sight width for the enemy (by degrees).");

            canEnterIndoors = Config.Bind("Modifiers", "Can enter the factory", true, "If shamblers can enter the factory at their own whim. Entry is chance based. The closer a shambler is to an entrance the more likely it will decide to enter.");
            disableColliderOnDeath = Config.Bind("Advanced", "Enemy Corpse Collision", false, "If a shambler's corpse should have its own collision box. You may want to keep this disabled if you have problems with the corpse getting in the way too often.");

            shipSafeZoneRadius = Config.Bind("Behavior", "Ship safe zone radius", 20f, "The radius of the protective sphere around the ship that the shambler can't pursue or attack in. Set to zero if you want some chaos, otherwise use as you please. The default value should protect you for about 5-ish steps from the edges of the ship.");
            stakeFreeChance = Config.Bind("Modifiers", "Stake Free Chance", 100, "Chance of a player to free themselves from a stake the shambler impaled them with. If you want the shambler to be extra punishing, take away some of the percentage chance here. Failures make the player take damage, and friends always have a 100% chance to free others. ");
            stakeFailDmg = Config.Bind("Modifiers", "Stake Fail Dmg", 20, "Amount of damage a player takes when they fail to free themselves from a stake. Irrelevant unless stake free chance is below 100%.");

            var spawnRateEntry = new FloatInputFieldConfigItem(soulRarity, new FloatInputFieldOptions
            {
                RequiresRestart = true,
                Min = 0.01f,
                Max = 100f,
            });


            var volumeSlider = new FloatSliderConfigItem(moaiGlobalMusicVol, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 1f
            });

            var speedSlider = new FloatSliderConfigItem(moaiGlobalSpeed, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 5f,
            });

            var LOSSlider = new FloatSliderConfigItem(LOSWidth, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 360f
            });

            var distEntry = new TextInputFieldConfigItem(devourerSpawnDist, new TextInputFieldOptions
            {
                RequiresRestart = true,
            });

            var maxEntry = new IntInputFieldConfigItem(maxCount, new IntInputFieldOptions
            {
                RequiresRestart = true,
                Min = 0,
                Max = 99999,
            });
            var healthEntry = new IntInputFieldConfigItem(health, new IntInputFieldOptions
            {
                RequiresRestart = false,
                Min = 1,
                Max = 99999,
            });
            var indoorsEntry = new BoolCheckBoxConfigItem(canEnterIndoors, new BoolCheckBoxOptions
            {
                RequiresRestart = false,
            });
            var disableColliderOnDeathEntry = new BoolCheckBoxConfigItem(disableColliderOnDeath, new BoolCheckBoxOptions
            {
                RequiresRestart = false,
            });

            var shipSafeZoneRadiusEntry = new FloatInputFieldConfigItem(shipSafeZoneRadius, new FloatInputFieldOptions
            {
                RequiresRestart = true,
                Min = 0.01f,
                Max = 100f,
            });
            var stakeFreeChanceEntry = new IntInputFieldConfigItem(stakeFreeChance, new IntInputFieldOptions
            {
                RequiresRestart = true,
                Min = 0,
                Max = 100,
            });
            var stakeFailDmgEntry = new IntInputFieldConfigItem(stakeFailDmg, new IntInputFieldOptions
            {
                RequiresRestart = true,
                Min = 0,
                Max = 1000,
            });


            LethalConfigManager.AddConfigItem(spawnRateEntry);
            LethalConfigManager.AddConfigItem(distEntry);
            LethalConfigManager.AddConfigItem(volumeSlider);
            //LethalConfigManager.AddConfigItem(sizeSlider);
            //LethalConfigManager.AddConfigItem(sizeVarSlider);
            LethalConfigManager.AddConfigItem(speedSlider);
            //LethalConfigManager.AddConfigItem(maxSizeEntry);
            //LethalConfigManager.AddConfigItem(daytimeSpawnEntry);
            LethalConfigManager.AddConfigItem(LOSSlider);
            LethalConfigManager.AddConfigItem(maxEntry);
            LethalConfigManager.AddConfigItem(healthEntry);
            //LethalConfigManager.AddConfigItem(pulseEntry);
            LethalConfigManager.AddConfigItem(indoorsEntry);
            LethalConfigManager.AddConfigItem(disableColliderOnDeathEntry);
            LethalConfigManager.AddConfigItem(shipSafeZoneRadiusEntry);
            LethalConfigManager.AddConfigItem(stakeFreeChanceEntry);
            LethalConfigManager.AddConfigItem(stakeFailDmgEntry);
            //LethalConfigManager.AddConfigItem(thrashSlider);
        }
    }
}