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
using MoaiEnemy.src.MoaiNormal;
using System.Collections.Generic;
using MoaiEnemy.src.Utilities;

namespace MoaiEnemy
{
    [BepInDependency("LethalNetworkAPI")]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static Harmony _harmony;
        public static EnemyType ExampleEnemy;
        public static new ManualLogSource Logger;

        public static MoaiNormalNet networkHandler = new MoaiNormalNet();

        // defined assets
        public static EnemyType MoaiEnemy;
        public static TerminalNode tlTerminalNode;
        public static TerminalKeyword tlTerminalKeyword;

        public static EnemyType MoaiBlue;
        public static TerminalNode MoaiBlueTerminalNode;
        public static TerminalKeyword MoaiBlueTerminalKeyword;

        public static EnemyType MoaiRed;
        public static TerminalNode MoaiRedTerminalNode;
        public static TerminalKeyword MoaiRedTerminalKeyword;

        public static float rawSpawnMultiplier = 0f;


        public void LogIfDebugBuild(string text)
        {
#if DEBUG
            Plugin.Logger.LogInfo(text);
#endif
        }

        private void Awake()
        {
            Logger = base.Logger;
            Assets.PopulateAssets();
            bindVars();

            // asset loading phase
            MoaiEnemy = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiEnemy");
            tlTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiEnemyTN");
            tlTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiEnemyTK");

            MoaiBlue = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiBlue");
            MoaiBlueTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiBlueTN");
            MoaiBlueTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiBlueTK");

            MoaiRed = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiRed");
            MoaiRedTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiRedTN");
            MoaiRedTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiRedTK");

            // debug phase
            Debug.Log("MOAI ENEMY BUNDLE: " + Assets.MainAssetBundle.ToString());
            Debug.Log("MOAINORM ENEMY: " + MoaiEnemy);
            Debug.Log("MOAINORM TK: " + tlTerminalKeyword);
            Debug.Log("MOAINORM TN: " + tlTerminalNode);
            Debug.Log("MOAIBLUE ENEMY: " + MoaiBlue);
            Debug.Log("MOAIBLUE TK: " + MoaiBlueTerminalNode);
            Debug.Log("MOAIBLUE TN: " + MoaiBlueTerminalKeyword);
            Debug.Log("RED ENEMY: " + MoaiRed);
            Debug.Log("RED TK: " + MoaiRedTerminalNode);
            Debug.Log("RED TN: " + MoaiRedTerminalKeyword);

            UnityEngine.Random.InitState((int)System.DateTime.Now.Ticks);

            // register phase 
            NetworkPrefabs.RegisterNetworkPrefab(MoaiEnemy.enemyPrefab);
            NetworkPrefabs.RegisterNetworkPrefab(MoaiBlue.enemyPrefab);
            NetworkPrefabs.RegisterNetworkPrefab(MoaiRed.enemyPrefab);

            // rarity range is 0-100 normally
            rawSpawnMultiplier = RawspawnHandler.getSpawnMultiplier();
            RegisterEnemy(MoaiEnemy, (int)(0), LevelTypes.All, SpawnType.Daytime, tlTerminalNode, tlTerminalKeyword);
            RegisterEnemy(MoaiBlue, (int)(0), LevelTypes.All, SpawnType.Outside, MoaiBlueTerminalNode, MoaiBlueTerminalKeyword);
            RegisterEnemy(MoaiRed, (int)(0), LevelTypes.All, SpawnType.Outside, MoaiRedTerminalNode, MoaiRedTerminalKeyword);

            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");


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
            Debug.Log("MOAI: Registering Moai Net Messages");
            networkHandler.setup();

            // actual logic for setting rarity
            On.RoundManager.LoadNewLevel += (On.RoundManager.orig_LoadNewLevel orig, global::RoundManager self, int randomSeed, global::SelectableLevel newLevel) =>
            {
                if (newLevel.PlanetName.Contains("Easter"))
                {
                    rawSpawnMultiplier = RawspawnHandler.getSpawnMultiplier(true);
                }
                else
                {
                    rawSpawnMultiplier = RawspawnHandler.getSpawnMultiplier();
                }

                var normPkg = new RawspawnHandler.enemyRarityPkg();
                normPkg.name = MoaiEnemy.name;
                normPkg.rarity = (int)(120 * baseRarity.Value * rawSpawnMultiplier);

                var bluePkg = new RawspawnHandler.enemyRarityPkg();
                bluePkg.name = MoaiBlue.name;
                bluePkg.rarity = (int)(46 * blueRarity.Value * rawSpawnMultiplier);

                var redPkg = new RawspawnHandler.enemyRarityPkg();
                redPkg.name = MoaiRed.name;
                redPkg.rarity = (int)(28 * redRarity.Value * rawSpawnMultiplier);

                RawspawnHandler.setLevelSpawnWeights([normPkg], [bluePkg, redPkg]);

                orig.Invoke(self, randomSeed, newLevel);
            };
        }

        // SETTINGS SECTION
        // consider these multipliers for existing values
        public static ConfigEntry<float> moaiGlobalSize;
        public static ConfigEntry<float> moaiGlobalSizeVar;
        public static ConfigEntry<float> moaiGlobalMusicVol;
        public static ConfigEntry<float> moaiGlobalSpeed;
        public static ConfigEntry<string> moaiSpawnDistribution;
        public static ConfigEntry<float> baseRarity;
        public static ConfigEntry<float> blueRarity;
        public static ConfigEntry<float> redRarity;

        public void bindVars()
        {
            moaiGlobalMusicVol = Config.Bind("Global", "Enemy Sound Volume", 0.6f, "Changes the volume of all moai sounds. May make moai more sneaky as well.");
            moaiGlobalSizeVar = Config.Bind("Global", "Size Variant Chance", 0.2f, "The chance of a moai to spawn in a randomly scaled size. Affects their pitch too.");
            moaiGlobalSize = Config.Bind("Global", "Size Multiplier", 1f, "Changes the size of all moai models. Scales pretty violently. Affects SFX pitch.");
            moaiGlobalSpeed = Config.Bind("Global", "Enemy Speed Multiplier", 1f, "Changes the speed of all moai. 4x would mean they are 4 times faster, 0.5x would be 2 times slower.");
            moaiSpawnDistribution = Config.Bind("Advanced", "Enemy Spawn Distribution", "4%100%, 6%50%, 10%25%", "For fine tuning spawn multipliers day to day. Value is a comma separated list. Each value follows the format C%M%, with C being the chance for the spawnrate multiplier to activate on a day (0-100%) and M being the multiplier (0-inf%). If a multiplier isn't activated, the spawnrate will be 0%.");
            baseRarity = Config.Bind("Variants", "Basic Moai Spawnrate", 1f, "Changes the spawnrate of the variant.");
            blueRarity = Config.Bind("Variants", "Blue Moai Spawnrate", 1f, "Changes the spawnrate of the variant.");
            redRarity = Config.Bind("Variants", "Red Moai Spawnrate", 1f, "Changes the spawnrate of the variant.");

            var sizeSlider = new FloatSliderConfigItem(moaiGlobalSize, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.05f,
                Max = 5f
            });

            var sizeVarSlider = new FloatSliderConfigItem(moaiGlobalSizeVar, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0f,
                Max = 1f
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


            var spawnEntry = new TextInputFieldConfigItem(moaiSpawnDistribution, new TextInputFieldOptions
            {
                RequiresRestart = false,
            });

            var baseEntry = new FloatInputFieldConfigItem(baseRarity, new FloatInputFieldOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 10000f,
            });

            var redEntry = new FloatInputFieldConfigItem(redRarity, new FloatInputFieldOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 10000f,
            });

            var blueEntry = new FloatInputFieldConfigItem(blueRarity, new FloatInputFieldOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 10000f,
            });

            LethalConfigManager.AddConfigItem(volumeSlider);
            LethalConfigManager.AddConfigItem(sizeSlider);
            LethalConfigManager.AddConfigItem(sizeVarSlider);
            LethalConfigManager.AddConfigItem(speedSlider);
            LethalConfigManager.AddConfigItem(baseEntry);
            LethalConfigManager.AddConfigItem(blueEntry);
            LethalConfigManager.AddConfigItem(redEntry);
            LethalConfigManager.AddConfigItem(spawnEntry);
        }

        public static class Assets
        {
            public static AssetBundle MainAssetBundle = null;
            public static void PopulateAssets()
            {
                string sAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                MainAssetBundle = AssetBundle.LoadFromFile(Path.Combine(sAssemblyLocation, "moaibundle"));
                if (MainAssetBundle == null)
                {
                    Plugin.Logger.LogError("Failed to load custom assets.");
                    return;
                }
            }
        }
    }
}