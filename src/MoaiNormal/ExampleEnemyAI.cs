using System;
using System.Collections;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;
using LC_API;
using static ExampleEnemy.Plugin;
using static ExampleEnemy.src.MoaiNormal.MoaiNormalNet;
using System.Linq;
using HarmonyLib;
using static UnityEngine.UIElements.UIR.Implementation.UIRStylePainter;
using System.Collections.Generic;

namespace ExampleEnemy.src.MoaiNormal
{

    // You may be wondering, how does the Example Enemy know it is from class ExampleEnemyAI?
    // Well, we give it a reference to to this class in the Unity project where we make the asset bundle.
    // Asset bundles cannot contain scripts, so our script lives here. It is important to get the
    // reference right, or else it will not find this file. See the guide for more information.

    class ExampleEnemyAI : EnemyAI
    {

        // ThunderMoai vars
        float ticksTillThunder = 5; // ticks occur 5 times per second
        int prefersFlesh = 0;

        // a non negative goodBoy meter means a friendly moai
        // very high values result in very generous acts
        // goodBoy goes down by 1 every AI tick (0.2 seconds).
        // more valuable scrap gives exponentially higher values
        int goodBoy = -1;
        Vector3 guardTarget = Vector3.zero;
        float impatience = 0;
        float wait = 20;

        // updated once every 15 seconds
        GrabbableObject[] source;
        int sourcecycle = 75;

        // extra audio sources
        public AudioSource creatureFood;
        public AudioSource creatureEat;
        public AudioSource creatureEatHuman;
        public AudioSource creatureHit;
        public AudioSource creatureDeath;
        bool eatingScrap = false;
        bool eatingHuman = false;
        int eatingTimer = -1;

        float rawSpawnProbability = 0.166f; // forced probability to spawn, affecting true spawnrate
        float groupSpawnChance = 0.1f;  // probability to form a group
        int rawSpawnGroup = 0; // enables group spawn, ignoring probability


        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
#pragma warning disable 0649
        public Transform turnCompass;
        public Transform attackArea;
#pragma warning restore 0649
        float timeSinceHittingLocalPlayer;
        float timeSinceNewRandPos;
        Vector3 positionRandomness;
        Vector3 StalkPos;
        System.Random enemyRandom;
        bool isDeadAnimationDone;
        enum State
        {
            SearchingForPlayer,
            Guard,
            StickingInFrontOfEnemy,
            StickingInFrontOfPlayer,
            HeadSwingAttackInProgress,
        }

        void LogIfDebugBuild(string text)
        {
#if DEBUG
            Plugin.Logger.LogInfo(text);
#endif
        }

        public void stopAllSound()
        {
            creatureSFX.Stop();
            creatureVoice.Stop();
            creatureEat.Stop();
            creatureEatHuman.Stop();
            creatureFood.Stop();
        }

        public override void Start()
        {
            // spawnrate control for strictly the daytime moai
            float trueSpawnProbability = rawSpawnProbability / moaiGlobalRarity.Value;
            if (!gameObject.name.Contains("Blue") && UnityEngine.Random.Range(0.0f, 1.0f) >= trueSpawnProbability && rawSpawnGroup <= 0)
            {
                LogIfDebugBuild("NORM/BLUE MOAI: spawncontrol -> probability failed at -> " + trueSpawnProbability * 100 + "%");
                Destroy(gameObject);
                return;
            }
            else
            {
                LogIfDebugBuild("NORM/BLUE MOAI: spawncontrol -> probability SUCCESS at -> " + trueSpawnProbability * 100 + "%");
                if (rawSpawnGroup > 0) { rawSpawnGroup--; }
                else if (UnityEngine.Random.Range(0.0f, 1.0f) <= groupSpawnChance)
                {
                    rawSpawnGroup = UnityEngine.Random.RandomRangeInt(1, 4);
                    LogIfDebugBuild("NORM/BLUE MOAI: Forming spawn cluster of size: " + rawSpawnGroup);
                }
            }
            base.Start();
            this.DoAnimationClientRpc("Idle");
            prefersFlesh = UnityEngine.Random.RandomRangeInt(0, 3);

            if (UnityEngine.Random.Range(0.0f, 1.0f) < 0.15)
            {
                goodBoy = UnityEngine.Random.RandomRangeInt(0, 7000);
                enemyHP += 4;
                LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
            }
            else
            {
                LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
            }

            // additional audio sources
            if (!creatureFood) { creatureFood = grabSource("CreatureFood") as AudioSource; }
            if (!creatureEat) { creatureEat = grabSource("CreatureEat") as AudioSource; }
            if (!creatureEatHuman) { creatureEatHuman = grabSource("CreatureEatHuman") as AudioSource; }
            if (!creatureHit) { creatureHit = grabSource("CreatureHit") as AudioSource; }
            if (!creatureDeath) { creatureDeath = grabSource("CreatureDeath") as AudioSource; }

            // size variant modification
            if (RoundManager.Instance.IsHost && UnityEngine.Random.Range(0.0f, 1.0f) <= moaiGlobalSizeVar.Value)
            {
                float newSize = 1;
                if (UnityEngine.Random.Range(0.0f, 1.0f) < 0.5f)
                { // small
                    newSize = 1 - UnityEngine.Random.Range(0.0f, 0.95f);
                }
                else
                { // large
                    newSize = 1 + UnityEngine.Random.Range(0.0f, 5.0f);
                }

                if (newSize < 1)
                {
                    var p = (double)newSize;
                    LC_API.Networking.Network.Broadcast("moaisizeset", new moaiSizePkg(NetworkObject.NetworkObjectId, newSize, (float)Math.Pow(p, 0.3)));
                }
                else
                {
                    LC_API.Networking.Network.Broadcast("moaisizeset", new moaiSizePkg(NetworkObject.NetworkObjectId, newSize, newSize));
                }
            }

            //LogIfDebugBuild("Moai Enemy Spawned");
            // account for config binds
            // creature sfx is music, while creature voice is idle noises (yes its weird)
            creatureVoice.volume = moaiGlobalVoiceVol.Value;
            creatureSFX.volume = moaiGlobalMusicVol.Value / 1.3f;
            creatureFood.volume = moaiGlobalMusicVol.Value;
            creatureEat.volume = moaiGlobalMusicVol.Value;
            creatureDeath.volume = moaiGlobalMusicVol.Value;
            creatureHit.volume = moaiGlobalMusicVol.Value;

            // enforce navmeshagent size
            if (RoundManager.Instance.IsHost)
            {
                if (moaiGlobalSize.Value < 1)
                {
                    var p = (double)moaiGlobalSize.Value;
                    LC_API.Networking.Network.Broadcast("moaisizeset", new moaiSizePkg(NetworkObject.NetworkObjectId, moaiGlobalSize.Value, (float)Math.Pow(p, 0.3)));
                }
                else
                {
                    LC_API.Networking.Network.Broadcast("moaisizeset", new moaiSizePkg(NetworkObject.NetworkObjectId, moaiGlobalSize.Value, moaiGlobalSize.Value));
                }
            }


            timeSinceHittingLocalPlayer = 0;
            //creatureAnimator.SetTrigger("startWalk");
            timeSinceNewRandPos = 0;
            positionRandomness = new Vector3(0, 0, 0);
            enemyRandom = new System.Random(StartOfRound.Instance.randomMapSeed + thisEnemyIndex);
            isDeadAnimationDone = false;

            // NOTE: Add your behavior states in your enemy script in Unity, where you can configure fun stuff
            // like a voice clip or an sfx clip to play when changing to that specific behavior state.
            currentBehaviourStateIndex = (int)State.SearchingForPlayer;
            // We make the enemy start searching. This will make it start wandering around.
            StartSearch(transform.position);
        }

        public void setHalo(bool active)
        {
            var halo = transform.Find("Halo");
            if (halo)
            {
                halo.gameObject.SetActive(active);
            }
            else
            {
                Debug.LogError("MOAI: failed to find Halo!");
            }
        }

        public override void Update()
        {
            base.Update();
            if (isEnemyDead)
            {
                // For some weird reason I can't get an RPC to get called from HitEnemy() (works from other methods), so we do this workaround. We just want the enemy to stop playing the song.
                if (!isDeadAnimationDone)
                {
                    //LogIfDebugBuild("Stopping enemy voice with janky code.");
                    isDeadAnimationDone = true;
                    creatureVoice.Stop();
                    creatureVoice.PlayOneShot(dieSFX);
                }
                return;
            }
            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceNewRandPos += Time.deltaTime;
            if (targetPlayer != null && PlayerIsTargetable(targetPlayer))
            {
                //Debug.Log("looking at player");
                turnCompass.LookAt(targetPlayer.gameplayCamera.transform.position);
            }
            if (stunNormalizedTimer > 0f)
            {
                agent.speed = 0f;
            }

            switch (currentBehaviourStateIndex)
            {
                case (int)State.SearchingForPlayer:
                    thunderReset();
                    break;

                case (int)State.StickingInFrontOfPlayer:
                    thunderTick();
                    break;
            };
        }

        public override void DoAIInterval()
        {
            //Debug.Log("AI Interval");
            base.DoAIInterval();
            if (isEnemyDead)
            {
                return;
            };
            goodBoy -= 1;

            // source update cycle
            if (sourcecycle > 0)
            {
                sourcecycle--;
            }
            else
            {
                //Debug.Log("MOAI: Refreshing Source -N- ");
                source = FindObjectsOfType<GrabbableObject>();
                sourcecycle = 75;
            }

            switch (currentBehaviourStateIndex)
            {
                case (int)State.SearchingForPlayer:
                    agent.speed = 3f * moaiGlobalSpeed.Value;

                    // sound switch
                    if (!creatureVoice.isPlaying)
                    {
                        //Debug.Log("MSOUND: creatureVoice");
                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureVoice"));
                    }

                    // good boy state switch
                    if (goodBoy > 0) {
                        StopSearch(currentSearch);
                        SwitchToBehaviourClientRpc((int)State.Guard);
                        LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
                    }

                    // object search and state switch;
                    if (getObj() || getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

                    if (FoundClosestPlayerInRange(28f))
                    {
                        //LogIfDebugBuild("Start Target Player");
                        StopSearch(currentSearch);
                        SwitchToBehaviourClientRpc((int)State.StickingInFrontOfPlayer);
                    }
                    break;
                case (int)State.Guard:
                    agent.speed = 4f * moaiGlobalSpeed.Value;

                    if(guardTarget == Vector3.zero)
                    {
                        impatience = 0;
                        wait = 20;
                        guardTarget = pickGuardNode().transform.position;
                    }
                    targetPlayer = null;
                    SetDestinationToPosition(guardTarget);

                    if (Vector3.Distance(transform.position, guardTarget) < (transform.localScale.magnitude + transform.localScale.magnitude + impatience))
                    {
                        if (wait <= 0)
                        {
                            guardTarget = Vector3.zero;
                        }
                        else
                        {
                            wait--;
                        }
                    }
                    else
                    {
                        impatience += 0.1f;
                    }

                    // sound switch
                    if (!creatureVoice.isPlaying)
                    {
                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureVoice"));
                    }

                    // good boy state switch
                    if (goodBoy <= 0) {
                        StartSearch(transform.position);
                        SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                        LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
                    }

                    // object search and state switch;
                    if (ClosestEnemyInRange(28)) { 
                        SwitchToBehaviourClientRpc((int)State.StickingInFrontOfEnemy); 
                    }
                    break;

                case (int)State.StickingInFrontOfEnemy:
                    agent.speed = 7f * moaiGlobalSpeed.Value;

                    var closestMonster = ClosestEnemyInRange(28);

                    // sound switch 
                    if (!creatureSFX.isPlaying)
                    {
                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureSFX"));
                    }

                    if (goodBoy <= 0) {
                        LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
                        SwitchToBehaviourClientRpc((int)State.SearchingForPlayer); 
                    }

                    if (!closestMonster) { SwitchToBehaviourClientRpc((int)State.Guard); }

                    // Charge into monster
                    StalkPos = closestMonster.transform.position;
                    SetDestinationToPosition(StalkPos, checkForPath: false);
                    break;
                case (int)State.StickingInFrontOfPlayer:
                    agent.speed = 5.3f * moaiGlobalSpeed.Value;

                    // sound switch 
                    if (!creatureSFX.isPlaying)
                    {
                        //Debug.Log("MSOUND: creatureSFX");
                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureSFX"));
                    }

                    // object search and state switch;
                    if (getObj() && prefersFlesh == 2) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }
                    if (getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

                    // good boy state switch
                    if (goodBoy > 0)
                    {
                        StopSearch(currentSearch);
                        SwitchToBehaviourClientRpc((int)State.Guard);
                        LC_API.Networking.Network.Broadcast("moaisethalo", new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
                    }

                    // Keep targetting closest player, unless they are over 20 units away and we can't see them.
                    if (!TargetClosestPlayerInAnyCase() && !FoundClosestPlayerInRange(28f))
                    {
                        //LogIfDebugBuild("Stop Target Player");
                        StartSearch(transform.position);
                        SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                        return;
                    }
                    StickingInFrontOfPlayer();
                    break;

                case (int)State.HeadSwingAttackInProgress:
                    // sound switch
                    if (!eatingHuman && !eatingScrap)
                    {
                        if (!creatureFood.isPlaying)
                        {
                            //Debug.Log("MSOUND: creatureFood");
                            LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureFood"));
                        }
                    }
                    else
                    {
                        if (!creatureEat.isPlaying && eatingScrap)
                        {
                            //Debug.Log("MSOUND: creatureEat");
                            LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEat"));
                        }
                        if (!creatureEatHuman.isPlaying && eatingHuman)
                        {
                            //Debug.Log("MSOUND: creatureEatHuman");
                            LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
                        }
                        if (eatingTimer > 0)
                        {
                            eatingTimer--;
                        }
                        else if (eatingTimer == 0)
                        {
                            GrabbableObject devouredObj = getObj();
                            if (devouredObj)
                            {
                                goodBoy = (int)Math.Pow(devouredObj.scrapValue * 1.5, 2.0);
                                enemyHP += (devouredObj.scrapValue / 10);
                                devouredObj.OnNetworkDespawn();
                                Destroy(devouredObj.NetworkObject);
                                Destroy(devouredObj.propBody);
                                Destroy(devouredObj.gameObject);
                                Destroy(devouredObj);
                            }

                            PlayerControllerB ply2 = getPlayerCorpse();
                            if (ply2)
                            {
                                LC_API.Networking.Network.Broadcast("moaidestroybody", new moaiDestroyBodyPkg(NetworkObject.NetworkObjectId, ply2.NetworkObject.NetworkObjectId));
                            }
                        }
                    }

                    // consumption
                    GrabbableObject obj = getObj();
                    PlayerControllerB ply = getPlayerCorpse();

                    if (obj == null && ply == null)
                    {
                        //Debug.Log("MOAI: Lost Object. Ending obj search.");
                        eatingHuman = false;
                        eatingScrap = false;
                        eatingTimer = -1;
                        StartSearch(transform.position);
                        SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                    }
                    else
                    {

                        if (ply)
                        {
                            //Debug.Log("MOAI: Heading to found Player");
                            targetPlayer = null;
                            targetNode = ply.deadBody.transform;
                            SetDestinationToPosition(ply.deadBody.transform.position);
                            if (Vector3.Distance(transform.position, ply.deadBody.transform.position) < ply.deadBody.transform.localScale.magnitude + transform.localScale.magnitude)
                            {
                                if (!eatingHuman)
                                {
                                    Debug.Log("MOAI: Attaching Body to Mouth");
                                    eatingTimer = 150;
                                    LC_API.Networking.Network.Broadcast("moaiattachbody", new moaiAttachBodyPkg(NetworkObject.NetworkObjectId, ply.NetworkObject.NetworkObjectId));
                                    LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
                                }
                                eatingHuman = true;
                            }
                            else
                            {
                                eatingHuman = false;
                            }
                        }
                        else if (obj)
                        {
                            //Debug.Log("MOAI: Heading to found Scrap");
                            targetPlayer = null;
                            targetNode = obj.transform;
                            SetDestinationToPosition(obj.transform.position);
                            if (Vector3.Distance(transform.position, obj.transform.position) < obj.transform.localScale.magnitude + transform.localScale.magnitude)
                            {
                                if (obj.IsLocalPlayer)
                                {
                                    if (!eatingHuman)
                                    {
                                        Debug.Log("MOAI: Attaching Body to Mouth");
                                        eatingTimer = 150;
                                        LC_API.Networking.Network.Broadcast("moaiattachbody", new moaiAttachBodyPkg(NetworkObject.NetworkObjectId, ply.NetworkObject.NetworkObjectId));
                                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
                                    }
                                    eatingHuman = true;
                                }
                                else if (!eatingScrap)
                                {
                                    eatingTimer = (int)(obj.scrapValue / 1.8) + 15;
                                    LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEat"));
                                }
                                eatingScrap = true;
                            }
                            else
                            {
                                eatingScrap = false;
                            }
                        }
                    }
                    if (!eatingHuman && !eatingScrap)
                    {
                        eatingTimer = -1;
                    }
                    break;

                default:
                    LogIfDebugBuild("This Behavior State doesn't exist!");
                    break;
            }
        }
        public PlayerControllerB getPlayerCorpse()
        {
            //Debug.Log("MOAI: Human Food Search");
            // look for human food first
            for (int i = 0; i < RoundManager.Instance.playersManager.allPlayerScripts.Length; i++)
            {
                PlayerControllerB player = RoundManager.Instance.playersManager.allPlayerScripts[i];

                if (player != null && player.name != null && player.transform != null)
                {

                    var d = 1000f;
                    if (player.deadBody != null && player.deadBody.isActiveAndEnabled && !player.deadBody.isInShip)
                    {
                        d = Vector3.Distance(transform.position, player.deadBody.transform.position);
                    }

                    //Debug.Log("MOAI: Human -> " + player.name + " dist - " + d + " dead? " + player.isPlayerDead);
                    if (d < 20.0f && player.deadBody != null && player.deadBody.isActiveAndEnabled && !player.deadBody.isInShip)
                    {
                        //Debug.Log("found player to eat");
                        return player;
                    }
                }
            }
            //Debug.Log("Can't eat anyone...");
            return null;
        }

        // return null if there are no valid objects to eat.
        // otherwise return a object
        public GrabbableObject getObj()
        {
            try
            {
                for (int i = 0; i < source.Length; i++)
                {
                    GrabbableObject obj = source[i];
                    //LogIfDebugBuild(obj.name);

                    if (Vector3.Distance(transform.position, obj.transform.position) < 20.0f && !obj.heldByPlayerOnServer && !obj.isInShipRoom)
                    {
                        //Debug.Log("MOAI: Returning object -> " + obj.name);
                        return obj;
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                //Debug.Log("MOAI: Refreshing Source -L- ");
                source = FindObjectsOfType<GrabbableObject>();
            }
            catch (NullReferenceException)
            {
                return null;
            }
            return null;   // no food :(
        }

        public GameObject pickGuardNode()
        {
            Debug.Log("MOAIGUARD: Picking Guard Node");
            List<GameObject> allGoodNodes = new List<GameObject>();

            foreach (GameObject g in allAINodes)
            {
                for(int i = 0; i < StartOfRound.Instance.allPlayerScripts.Length; i++)
                {
                    Vector3 playerPos = StartOfRound.Instance.allPlayerScripts[i].gameObject.transform.position;
                    //Debug.Log(playerPos);
                    //Debug.Log(g.transform.position);
                    float dist = Vector3.Distance(g.transform.position, playerPos);
                    //Debug.Log("Dist: " + dist);
                    //Debug.Log("dist < " + (23 + this.transform.localScale.x));
                    if (dist < (23 + this.transform.localScale.x))
                    {
                        allGoodNodes.Add(g);
                        //Debug.Log("appended to good node -> " + allGoodNodes.Count + " - " + allGoodNodes.ToString());
                    }
                }
            }
            if(allGoodNodes.Count > 0)
            {
                Debug.Log("MOAIGUARD: Returning Good Node");
                return allGoodNodes[UnityEngine.Random.RandomRangeInt(0, allGoodNodes.Count)];
            }
            else
            {
                Debug.Log("MOAIGUARD: Returning Random Node");
                return allAINodes[UnityEngine.Random.RandomRangeInt(0, allAINodes.Length)];
            }
        }

        public void thunderReset()
        {
            RoundManager m = RoundManager.Instance;

            if (!gameObject.name.Contains("Blue"))
            {
                return;
            }

            if (targetPlayer == null || ticksTillThunder > 0)
            {
                return;
            }

            //LogIfDebugBuild("MOAI: spawning LBolt");
            ticksTillThunder = 2 + Math.Min((float)Math.Pow(Vector3.Distance(transform.position, targetPlayer.transform.position), 1.75), 180);
            Vector3 position = serverPosition;
            position.y += (float)(enemyRandom.NextDouble() * ticksTillThunder * 0.2 + 4 * this.gameObject.transform.localScale.x) * Math.Sign(enemyRandom.Next(-100, 100));
            position.x += (float)(enemyRandom.NextDouble() * ticksTillThunder * 0.2 + 4 * this.gameObject.transform.localScale.x) * Math.Sign(enemyRandom.Next(-100, 100));

            GameObject weather = GameObject.Find("TimeAndWeather");

            // find "Stormy" in weather
            GameObject striker = null;
            for (int i = 0; i < weather.transform.GetChildCount(); i++)
            {
                GameObject g = weather.transform.GetChild(i).gameObject;
                if (g.name.Equals("Stormy"))
                {
                    //Debug.Log("Lethal Chaos: Found Stormy!");
                    striker = g;
                }
            }
            if (striker != null)
            {
                // change to include warning
                striker.SetActive(true);
                m.LightningStrikeServerRpc(position);
                
                //m.ShowStaticElectricityWarningClientRpc
            }
            else
            {
                Debug.LogError("Lethal Chaos: Failed to find Stormy Weather container (LBolt)!");
            }
        }
        public void thunderTick()
        {
            ticksTillThunder -= 1;
            if (ticksTillThunder <= 0)
            {
                thunderReset();
            }
        }

        bool FoundClosestPlayerInRange(float range)
        {
            TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: true);
            if (targetPlayer == null) return false;
            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < range;
        }

        EnemyAI ClosestEnemyInRange(float range)
        {

            var enemies = RoundManager.Instance.SpawnedEnemies;
            var closestDist = range;
            EnemyAI closestEnemy = null;
            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];

                // only target evil moai from the list
                if (enemy.gameObject.name.ToLower().Contains("moai"))
                {
                    if (enemy.transform.Find("Halo"))
                    {
                        if (!enemy.transform.Find("Halo").gameObject.activeSelf)
                        {
                            var dist = Vector3.Distance(transform.position, enemy.transform.position);
                            if (dist < closestDist && enemy.enemyHP > 0 && !enemy.isEnemyDead && enemy.GetInstanceID() != GetInstanceID())
                            {
                                closestDist = dist;
                                closestEnemy = enemy;
                            }
                        }
                    }
                }
                else // target enemies in general
                {
                    var dist = Vector3.Distance(transform.position, enemy.transform.position);
                    if (dist < closestDist && enemy.enemyHP > 0 && !enemy.isEnemyDead)
                    {
                        closestDist = dist;
                        closestEnemy = enemy;
                    }
                }
            }
            if (closestEnemy != null && !closestEnemy.isEnemyDead && closestEnemy.enemyHP > 0 && closestEnemy.enemyType.canDie)
            {
                if (!closestEnemy.gameObject.name.ToLower().Contains("locust"))  // dumb locusts
                {
                    return closestEnemy;
                }
            }
            return null;
        }
        
        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            if (this.isEnemyDead)
            {
                return;
            }
            this.enemyHP -= force;
            if (base.IsOwner)
            {
                if (this.enemyHP <= 0)
                {
                    base.KillEnemyOnOwnerClient(false);
                    this.stopAllSound();
                    this.DoAnimationClientRpc("Death");
                    isEnemyDead = true;
                    LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureDeath"));
                    return;
                }
                LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureHit"));
            }
        }

        bool TargetClosestPlayerInAnyCase()
        {
            mostOptimalDistance = 20f;
            targetPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                tempDist = Vector3.Distance(transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                if (tempDist < mostOptimalDistance)
                {
                    mostOptimalDistance = tempDist;
                    targetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                }
            }
            if (targetPlayer == null) return false;
            return true;
        }

        void StickingInFrontOfPlayer()
        {
            // We only run this method for the host because I'm paranoid about randomness not syncing I guess
            // This is fine because the game does sync the position of the enemy.
            // Also the attack is a ClientRpc so it should always sync
            if (targetPlayer == null || !IsOwner)
            {
                return;
            }

            // Charge into player
            StalkPos = targetPlayer.transform.position;
            SetDestinationToPosition(StalkPos, checkForPath: false);
        }

        public override void OnCollideWithEnemy(Collider other, EnemyAI collidedEnemy = null)
        {
            if (this.timeSinceHittingLocalPlayer < 0.5f || collidedEnemy.isEnemyDead || isEnemyDead)
            {
                return;
            }
            if(collidedEnemy.gameObject.name.ToLower().Contains("moai") && transform.Find("Halo") && collidedEnemy.transform.Find("Halo"))
            {
                // halos don't hit halos, non-halos don't hit non-halos
                if(transform.Find("Halo").gameObject.activeSelf == collidedEnemy.transform.Find("Halo").gameObject.activeSelf)
                {
                    return;
                }
            }
            this.timeSinceHittingLocalPlayer = 0f;
            collidedEnemy.HitEnemy(2, null, true);
        }


        public override void OnCollideWithPlayer(Collider other)
        {
            if (timeSinceHittingLocalPlayer < 0.5f)
            {
                return;
            }
            PlayerControllerB playerControllerB = MeetsStandardPlayerCollisionConditions(other);
            if (playerControllerB != null)
            {
                //LogIfDebugBuild("Example Enemy Collision with Player!");
                timeSinceHittingLocalPlayer = 0f;
                if (goodBoy <= 0)
                {
                    if (playerControllerB.health < 30)
                    {
                        playerControllerB.KillPlayer(playerControllerB.velocityLastFrame, true, CauseOfDeath.Mauling, 0);
                    }
                    else
                    {
                        playerControllerB.DamagePlayer(30);
                    }
                }
                else // normal moai good boy effect is healing
                {
                    if (goodBoy > 0 && playerControllerB.health <= 90)
                    {
                        playerControllerB.DamagePlayer(-10);
                    }
                }
            }
        }

        public AudioSource grabSource(string argname)
        {
            var sources = GetComponentsInChildren<AudioSource>();
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource s = sources[i];
                if (s.name.Equals(argname))
                {
                    return s;
                }
            }
            return null;
        }

        [ClientRpc]
        public void DoAnimationClientRpc(string animationName)
        {
            LogIfDebugBuild($"Animation: {animationName}");
            if (this.gameObject.GetComponent<Animator>()) { this.gameObject.GetComponent<Animator>().Play(animationName); }
        }

    }
}