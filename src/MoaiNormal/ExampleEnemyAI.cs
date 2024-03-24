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

        // updated once every 15 seconds
        GrabbableObject[] source;
        int sourcecycle = 75;

        // extra audio sources
        public AudioSource creatureFood;
        public AudioSource creatureEat;
        public AudioSource creatureEatHuman;
        bool eatingScrap = false;
        bool eatingHuman = false;
        int eatingTimer = -1;

        float rawSpawnProbability = 0.166f; // forced probability to spawn, affecting true spawnrate
        float groupSpawnChance = 0.18f;  // probability to form a group
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
                LogIfDebugBuild("MOAI: spawncontrol -> probability failed at -> " + trueSpawnProbability * 100 + "%");
                Destroy(gameObject);
                return;
            }
            else
            {
                LogIfDebugBuild("MOAI: spawncontrol -> probability SUCCESS at -> " + trueSpawnProbability * 100 + "%");
                if (rawSpawnGroup > 0) { rawSpawnGroup--; }
                else if (UnityEngine.Random.Range(0.0f, 1.0f) <= groupSpawnChance)
                {
                    rawSpawnGroup = UnityEngine.Random.RandomRangeInt(1, 4);
                    LogIfDebugBuild("MOAI: Forming spawn cluster of size: " + rawSpawnGroup);
                }
            }
            base.Start();

            // additional audio sources
            if (!creatureFood) { creatureFood = grabSource("CreatureFood") as AudioSource; }
            if (!creatureEat) { creatureEat = grabSource("CreatureEat") as AudioSource; }
            if (!creatureEatHuman) { creatureEatHuman = grabSource("CreatureEatHuman") as AudioSource; }

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

                    // object search and state switch;
                    if (getObj() || getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

                    if (FoundClosestPlayerInRange(28f))
                    {
                        //LogIfDebugBuild("Start Target Player");
                        StopSearch(currentSearch);
                        SwitchToBehaviourClientRpc((int)State.StickingInFrontOfPlayer);
                    }
                    break;

                case (int)State.StickingInFrontOfPlayer:
                    agent.speed = 5.3f * moaiGlobalSpeed.Value;

                    // sound switch 
                    if (!creatureSFX.isPlaying)
                    {
                        //Debug.Log("MSOUND: creatureSFX");
                        LC_API.Networking.Network.Broadcast("moaisoundplay", new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureSFX"));
                    }
                    thunderTick();

                    // object search and state switch;
                    if (getObj() || getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

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
                                devouredObj.OnNetworkDespawn();
                                Destroy(devouredObj.NetworkObject);
                                Destroy(devouredObj.propBody);
                                Destroy(devouredObj.gameObject);
                                Destroy(devouredObj);
                            }

                            PlayerControllerB ply2 = getPlayerCorpse();
                            if (ply2)
                            {
                                ply2.deadBody.DeactivateBody(false);
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

                    var d = Vector3.Distance(transform.position, player.transform.position);
                    if (player.deadBody != null && player.deadBody.isActiveAndEnabled)
                    {
                        d = Vector3.Distance(transform.position, player.deadBody.transform.position);
                    }

                    //Debug.Log("MOAI: Human -> " + player.name + " dist - " + d + " dead? " + player.isPlayerDead);
                    if (d < 20.0f && player.deadBody != null && player.deadBody.isActiveAndEnabled)
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

                    if (Vector3.Distance(transform.position, obj.transform.position) < 20.0f && !obj.heldByPlayerOnServer)
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
            ticksTillThunder = Math.Min((float)Math.Pow(Vector3.Distance(transform.position, targetPlayer.transform.position), 1.75), 180);
            Vector3 position = serverPosition;
            position.y += (float)(enemyRandom.NextDouble() * ticksTillThunder * 0.2) - ticksTillThunder * 0.1f;
            position.x += (float)(enemyRandom.NextDouble() * ticksTillThunder * 0.2) - ticksTillThunder * 0.1f;

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

            //  maybe better if  1.5f?
            TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: true);
            if (targetPlayer == null) return false;
            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < range;
        }

        bool TargetClosestPlayerInAnyCase()
        {
            mostOptimalDistance = 23f;
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
                if (playerControllerB.health < 30)
                {
                    playerControllerB.KillPlayer(playerControllerB.velocityLastFrame, true, CauseOfDeath.Mauling, 0);
                }
                else
                {
                    playerControllerB.DamagePlayer(30);
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
            //LogIfDebugBuild($"Animation: {animationName}");
            //creatureAnimator.SetTrigger(animationName);
        }

    }
}