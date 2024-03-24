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
using static UnityEngine.GraphicsBuffer;
using System;
using UnityEngine.AI;
using GameNetcodeStuff;
using ExampleEnemy.src.MoaiNormal;

namespace ExampleEnemy
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)] 
    public class Plugin : BaseUnityPlugin {
        public static Harmony _harmony; 
        public static EnemyType ExampleEnemy;
        public static new ManualLogSource Logger;

        public void LogIfDebugBuild(string text)
        {
        #if DEBUG
            Plugin.Logger.LogInfo(text);
        #endif
        }

        private void Awake() {
            Logger = base.Logger;
            Assets.PopulateAssets();
            bindVars();

            // asset loading phase
            var ExampleEnemy = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiEnemy");
            var tlTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiEnemyTN");
            var tlTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiEnemyTK");

            var MoaiBlue = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiBlue");
            var MoaiBlueTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiBlueTN");
            var MoaiBlueTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiBlueTK");

            var MoaiRed = Assets.MainAssetBundle.LoadAsset<EnemyType>("MoaiRed");
            var MoaiRedTerminalNode = Assets.MainAssetBundle.LoadAsset<TerminalNode>("MoaiRed");
            var MoaiRedTerminalKeyword = Assets.MainAssetBundle.LoadAsset<TerminalKeyword>("MoaiRed");

            // debug phase
            Debug.Log("EX BUNDLE: " + Assets.MainAssetBundle.ToString());
            Debug.Log("EX ENEMY: " + ExampleEnemy);
            Debug.Log("EX TK: " + tlTerminalKeyword);
            Debug.Log("EX TN: " + tlTerminalNode);
            Debug.Log("BLUE ENEMY: " + MoaiBlue);
            Debug.Log("BLUE TK: " + MoaiBlueTerminalNode);
            Debug.Log("BLUE TN: " + MoaiBlueTerminalKeyword);
            Debug.Log("RED ENEMY: " + MoaiRed);
            Debug.Log("RED TK: " + MoaiRedTerminalNode);
            Debug.Log("RED TN: " + MoaiRedTerminalKeyword);

            // register phase 
            NetworkPrefabs.RegisterNetworkPrefab(ExampleEnemy.enemyPrefab);
            NetworkPrefabs.RegisterNetworkPrefab(MoaiBlue.enemyPrefab);

            // rarity range is 0-100 normally
            RegisterEnemy(ExampleEnemy, (int)(14 / moaiGlobalRarity.Value), LevelTypes.All, SpawnType.Daytime, tlTerminalNode, tlTerminalKeyword);
            RegisterEnemy(MoaiBlue, (int)(27 / moaiGlobalRarity.Value), LevelTypes.All, SpawnType.Outside, MoaiBlueTerminalNode, MoaiBlueTerminalKeyword);
            RegisterEnemy(MoaiRed, (int)(6 / moaiGlobalRarity.Value), LevelTypes.All, SpawnType.Outside, MoaiRedTerminalNode, MoaiRedTerminalKeyword); 
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");

            // Required by https://github.com/EvaisaDev/UnityNetcodePatcher maybe?
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
            MoaiNormalNet.setup();
            MoaiRedNet.setup();
        }

        // SETTINGS SECTION
        // consider these multipliers for existing values
        public static ConfigEntry<float> moaiGlobalSize;
        public static ConfigEntry<float> moaiGlobalSizeVar;
        public static ConfigEntry<float> moaiGlobalMusicVol;
        public static ConfigEntry<float> moaiGlobalVoiceVol;
        public static ConfigEntry<float> moaiGlobalRarity;
        public static ConfigEntry<float> moaiGlobalSpeed;

        public void bindVars()
        {
            moaiGlobalMusicVol = Config.Bind("Global", "Chase Sound Volume", 0.6f, "Changes the volume of the MOAHHHH sound during chase. Also affects all chase sound variants.");
            moaiGlobalVoiceVol = Config.Bind("Global", "Idle Sound Volume", 0.6f, "Changes the volume of moai sounds when they aren't chasing you. Changing this could make moai more or less sneaky.");
            moaiGlobalSizeVar = Config.Bind("Global", "Size Variant Chance", 0.2f, "The chance of a moai to spawn in a randomly scaled size. Affects their pitch too.");
            moaiGlobalSize = Config.Bind("Global", "Size Multiplier", 1f, "Changes the size of all moai models. Scales pretty violently. Affects SFX pitch.");
            moaiGlobalRarity = Config.Bind("Global", "Enemy Rarity Multiplier", 1f, "How rare are moai? A 2x multiplier makes them 2x more rare, and a 0.25x multiplier would make them 4x more common.");
            moaiGlobalSpeed = Config.Bind("Global", "Enemy Speed Multiplier", 1f, "Changes the speed of all moai. 4x would mean they are 4 times faster, 0.5x would be 2 times slower.");

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

            var volume2Slider = new FloatSliderConfigItem(moaiGlobalVoiceVol, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 1f
            });

            var raritySlider = new FloatSliderConfigItem(moaiGlobalRarity, new FloatSliderOptions
            {
                RequiresRestart = true,
                Min = 0.05f,
                Max = 10f
            });

            var speedSlider = new FloatSliderConfigItem(moaiGlobalSpeed, new FloatSliderOptions
            {
                RequiresRestart = false,
                Min = 0.0f,
                Max = 5f,
            });
            
            LethalConfigManager.AddConfigItem(volumeSlider);
            LethalConfigManager.AddConfigItem(volume2Slider);
            LethalConfigManager.AddConfigItem(sizeSlider);
            LethalConfigManager.AddConfigItem(sizeVarSlider);
            LethalConfigManager.AddConfigItem(raritySlider);
            LethalConfigManager.AddConfigItem(speedSlider);
        }
    }

    public static class Assets {
        public static AssetBundle MainAssetBundle = null;
        public static void PopulateAssets() {
            string sAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            MainAssetBundle = AssetBundle.LoadFromFile(Path.Combine(sAssemblyLocation, "moaibundle"));
            if (MainAssetBundle == null) {
                Plugin.Logger.LogError("Failed to load custom assets.");
                return;
            }
        }
    }
}