using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using static MoaiEnemy.src.MoaiNormal.MoaiNormalNet;
using static MoaiEnemy.Plugin;
using Unity.Netcode;
using UnityEngine.AI;
using MoaiEnemy.src.Utilities;

namespace MoaiEnemy.src.MoaiNormal
{
    // using this to gain more control over the ai system...
    internal abstract class MOAIAICORE : EnemyAI
    {
        protected Animator animator;

        // ThunderMoai vars
        protected float ticksTillThunder = 5; // ticks occur 5 times per second

        // a non negative goodBoy meter means a friendly moai
        // very high values result in very generous acts
        // goodBoy goes down by 1 every AI tick (0.2 seconds).
        // more valuable scrap gives exponentially higher values
        protected int goodBoy = -1;
        protected Vector3 guardTarget = Vector3.zero;
        protected float impatience = 0;
        protected float wait = 20;

        // related to entering and exiting entrances
        // updated once every 4-ish seconds
        protected EntranceTeleport nearestEntrance = null;
        protected PlayerControllerB mostRecentPlayer = null;
        protected int entranceDelay = 0;  // prevents constant rentry / exit
        protected float chanceToLocateEntrancePlayerBonus = 0;
        protected float chanceToLocateEntrance = 0;

        // updated once every 15 seconds
        protected GrabbableObject[] source;
        protected int sourcecycle = 75;

        // extra audio sources
        public AudioSource creatureFood;
        public AudioSource creatureEat;
        public AudioSource creatureEatHuman;
        public AudioSource creatureHit;
        public AudioSource creatureDeath;
        public AudioSource creatureBelch;
        public AudioSource slidingBasic;
        public AudioSource slidingWood;
        public AudioSource slidingSnow;
        public AudioSource slidingMetal;
        public AudioSource slidingGravel;
        public bool isSliding = false;
        public Transform mouth;
        protected bool eatingScrap = false;
        protected bool eatingHuman = false;
        protected int eatingTimer = -1;

        // stamina mechancis
        protected float stamina = 0; // moai use stamina to chase the player
        bool recovering = false; // moai don't chase if they are recovering
        public int provokePoints = 0;

#pragma warning disable 0649
        public Transform turnCompass;
        public Transform attackArea;
#pragma warning restore 0649
        protected float timeSinceHittingLocalPlayer;
        protected float timeSinceNewRandPos;
        protected Vector3 positionRandomness;
        protected Vector3 StalkPos;
        protected System.Random enemyRandom;
        protected bool isDeadAnimationDone;

        public enum State
        {
            SearchingForPlayer,
            Guard,
            StickingInFrontOfEnemy,
            StickingInFrontOfPlayer,
            HeadSwingAttackInProgress,
            HeadingToEntrance,
        }

        public void LogIfDebugBuild(string text)
        {
#if DEBUG
            Plugin.Logger.LogInfo(text);
#endif
        }

        public virtual void setPitches(float pitchAlter)
        {
            // do nothing
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

        public void baseInit()
        {
            mostRecentPlayer = this.GetClosestPlayer();
            animator = this.gameObject.GetComponent<Animator>();

            base.Start();
            if (RoundManager.Instance.IsServer)
            {
                this.DoAnimationClientRpc(0);
            }
            else
            {
                // animations are handled strictly through the server
                this.animator.enabled = false;
            }

            if (UnityEngine.Random.Range(0.0f, 1.0f) < 0.15)
            {
                goodBoy = UnityEngine.Random.RandomRangeInt(0, 7000);
                enemyHP += 4;
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
            }
            else
            {
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
            }

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
                    Plugin.networkHandler.s_moaiSizeSet.SendAllClients(new moaiSizePkg(NetworkObject.NetworkObjectId, newSize, (float)Math.Pow(p, 0.3)));
                }
                else
                {
                    Plugin.networkHandler.s_moaiSizeSet.SendAllClients(new moaiSizePkg(NetworkObject.NetworkObjectId, newSize, newSize));
                }
            }

            // adjust volume according to config bind
            creatureVoice.volume = moaiGlobalMusicVol.Value;
            creatureSFX.volume = moaiGlobalMusicVol.Value / 1.3f;
            creatureFood.volume = moaiGlobalMusicVol.Value;
            creatureEat.volume = moaiGlobalMusicVol.Value;
            creatureDeath.volume = moaiGlobalMusicVol.Value;
            creatureHit.volume = moaiGlobalMusicVol.Value;
            creatureEatHuman.volume = moaiGlobalMusicVol.Value;
            creatureBelch.volume = moaiGlobalMusicVol.Value;
            slidingBasic.volume = moaiGlobalMusicVol.Value;
            slidingGravel.volume = moaiGlobalMusicVol.Value;
            slidingMetal.volume = moaiGlobalMusicVol.Value;
            slidingSnow.volume = moaiGlobalMusicVol.Value;
            slidingWood.volume = moaiGlobalMusicVol.Value / 1.3f;

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

            Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureBelch"));
            Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureVoice"));
        }

        public void baseUpdate() {
            if (isEnemyDead)
            {
                if (!isDeadAnimationDone)
                {
                    isDeadAnimationDone = true;
                    stopAllSound();
                    creatureVoice.PlayOneShot(dieSFX);
                }
                return;
            }

            if(targetPlayer != null && targetPlayer.isPlayerDead) { targetPlayer = null; }
            movingTowardsTargetPlayer = targetPlayer != null;

            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceNewRandPos += Time.deltaTime;
            if (targetPlayer != null && PlayerIsTargetable(targetPlayer))
            {
                turnCompass.LookAt(targetPlayer.gameplayCamera.transform.position);
            }
            if (stunNormalizedTimer > 0f)
            {
                agent.speed = 0f;
            }

            if (!isEnemyDead)
            {
                if (eatingTimer > 0)
                {
                    if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(2); }
                    this.animator.speed = 1.5f;
                }
                else if (agent.velocity.magnitude > (agent.speed / 4))
                {
                    if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(1); }
                    this.animator.speed = agent.velocity.magnitude / 3;
                }
                else if (agent.velocity.magnitude <= (agent.speed / 4))
                {
                    if (RoundManager.Instance.IsServer) { DoAnimationClientRpc(0); }
                    this.animator.speed = 1;
                }
            }
        }

        public void baseAIInterval()
        {
            goodBoy -= 1;
            if(provokePoints > 0)
            {
                provokePoints--;
            }
            if (entranceDelay > 0) { entranceDelay--; }
            slidingSoundTick();

            // source update cycle
            if (sourcecycle > 0)
            {
                sourcecycle--;
            }
            else
            {
                source = FindObjectsOfType<GrabbableObject>();
                sourcecycle = 75;
            }

            if(stamina <= 0)
            {
                recovering = true;
            }
            else if (stamina > 60)
            {
                recovering = false;
            }

            // executes once every second
            if (sourcecycle % 5 == 0)
            {
                nearestEntrance = EntityWarp.findNearestEntrance(this);

                if(stamina < 120)
                {
                    stamina += 3;  // a moai regenerates all of its stamina in 30 seconds?
                }

                if (currentBehaviourStateIndex == (int)State.Guard || currentBehaviourStateIndex == (int)State.StickingInFrontOfEnemy)
                {
                    mostRecentPlayer = this.GetClosestPlayer();
                }
            }

            // bug fix
            if (transform.Find("Halo").gameObject.activeSelf && goodBoy <= 0)
            {
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
            }

            if (targetPlayer != null)
            {
                mostRecentPlayer = targetPlayer;
            }
        }

        public void baseSearchingForPlayer()
        {
            agent.speed = 3f * moaiGlobalSpeed.Value;

            // sound switch
            if (!creatureVoice.isPlaying)
            {
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureVoice"));
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureBelch"));
            }

            // good boy state switch
            if (goodBoy > 0)
            {
                StopSearch(currentSearch);
                guardTarget = Vector3.zero;
                SwitchToBehaviourClientRpc((int)State.Guard);
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
            }

            // entrance state switch
            updateEntranceChance();
            if (this.enemyRandom.NextDouble() < chanceToLocateEntrance && gameObject.transform.localScale.x <= 2.2f)
            {
                Debug.Log("MOAI: entrance state switch");
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.HeadingToEntrance);
            }

            // object search and state switch;
            if (getObj() || getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

            if (FoundClosestPlayerInRange(28f, true) || provokePoints > 0)
            {
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.StickingInFrontOfPlayer);
            }
        }

        public void baseHeadingToEntrance()
        {
            targetPlayer = null;
            //Debug.Log("Heading to Entrance...");
            //Debug.Log(Vector3.Distance(transform.position, nearestEntrance.transform.position));
            SetDestinationToPosition(nearestEntrance.transform.position);
            if (this.isOutside != nearestEntrance.isEntranceToBuilding || this.agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                //Debug.Log("Entrance is not in navigation zone... Cancelling state");
                if (goodBoy <= 0)
                {
                    entranceDelay = 150;
                    StartSearch(transform.position);
                    SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                }
                else
                {
                    entranceDelay = 20;
                    StopSearch(currentSearch);
                    guardTarget = Vector3.zero;
                    SwitchToBehaviourClientRpc((int)State.Guard);
                }
            }
            if (Vector3.Distance(transform.position, nearestEntrance.transform.position) < (2.0 + gameObject.transform.localScale.x))
            {
                if (nearestEntrance.isEntranceToBuilding)
                {
                    Debug.Log("MOAI: Warp in");
                    EntityWarp.SendEnemyInside(this);
                    nearestEntrance.PlayAudioAtTeleportPositions();
                }
                else
                {
                    Debug.Log("MOAI: Warp out");
                    EntityWarp.SendEnemyOutside(this, true);
                    nearestEntrance.PlayAudioAtTeleportPositions();
                }
                if (goodBoy <= 0)
                {
                    entranceDelay = 150;
                    StartSearch(transform.position);
                    SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                }
                else
                {
                    entranceDelay = 20;
                    StopSearch(currentSearch);
                    guardTarget = Vector3.zero;
                    SwitchToBehaviourClientRpc((int)State.Guard);
                }
            }
        }

        public void baseGuard()
        {
            targetPlayer = null;
            agent.speed = 4f * moaiGlobalSpeed.Value;

            if (provokePoints > 0)
            {
                goodBoy = 0;
            }

            if (guardTarget == Vector3.zero)
            {
                impatience = 0;
                wait = 20;
                guardTarget = pickGuardNode().transform.position;
            }

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

            // prevents issues with struggling to reach a destination
            if (impatience == 10)
            {
                guardTarget = Vector3.zero;
            }

            // simply follow the player outside... with a delay
            if (mostRecentPlayer && mostRecentPlayer.isInsideFactory == this.isOutside && entranceDelay <= 0)
            {
                Debug.Log("MOAI: entrance state switch");
                StopSearch(currentSearch);
                SwitchToBehaviourClientRpc((int)State.HeadingToEntrance);
            }

            // sound switch
            if (!creatureVoice.isPlaying)
            {
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureVoice"));
            }

            // good boy state switch
            if (goodBoy <= 0)
            {
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
            }

            // object search and state switch;
            if (ClosestEnemyInRange(28))
            {
                SwitchToBehaviourClientRpc((int)State.StickingInFrontOfEnemy);
            }
        }

        public void baseStickingInFrontOfEnemy()
        {
            targetPlayer = null;
            agent.speed = 7f * moaiGlobalSpeed.Value;
            var closestMonster = ClosestEnemyInRange(28);
            this.stamina -= 1.5f;  // all stamina (150) is lost in 15 seconds?

            // sound switch 
            if (!creatureSFX.isPlaying)
            {
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureSFX"));
            }

            if (goodBoy <= 0)
            {
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, false));
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }

            if (!closestMonster)
            {
                StopSearch(currentSearch);
                guardTarget = Vector3.zero;
                SwitchToBehaviourClientRpc((int)State.Guard);
            }

            // Charge into monster
            StalkPos = closestMonster.transform.position;
            SetDestinationToPosition(StalkPos, checkForPath: false);
        }

        public void baseStickingInFrontOfPlayer() {
            agent.speed = 5.3f * moaiGlobalSpeed.Value;
            updateEntranceChance();

            this.stamina -= 1.5f;  // all stamina (150) is lost in 15 seconds?

            // sound switch 
            if (!creatureSFX.isPlaying)
            {
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureSFX"));
            }

            // object search and state switch;
            if (getPlayerCorpse()) { SwitchToBehaviourClientRpc((int)State.HeadSwingAttackInProgress); }

            // good boy state switch
            if (goodBoy > 0)
            {
                StopSearch(currentSearch);
                guardTarget = Vector3.zero;
                SwitchToBehaviourClientRpc((int)State.Guard);
                Plugin.networkHandler.s_moaiHalo.SendAllClients(new moaiHaloPkg(NetworkObject.NetworkObjectId, true));
            }

            // Keep targetting closest player, unless they are over 20 units away and we can't see them.
            if (!FoundClosestPlayerInRange(22f, false) && !FoundClosestPlayerInRange(28f, true) && provokePoints <= 0)
            {
                targetPlayer = null;
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
                return;
            }
            StickingInFrontOfPlayer();
        }

        public void baseHeadSwingAttackInProgress()
        {
            // sound switch
            if (!eatingHuman && !eatingScrap)
            {
                if (!creatureFood.isPlaying)
                {
                    //Debug.Log("MSOUND: creatureFood");
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureFood"));
                }
            }
            else
            {
                if (!creatureEat.isPlaying && eatingScrap)
                {
                    //Debug.Log("MSOUND: creatureEat");
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEat"));
                }
                if (!creatureEatHuman.isPlaying && eatingHuman)
                {
                    //Debug.Log("MSOUND: creatureEatHuman");
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
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
                        goodBoy = (int)Math.Pow(devouredObj.scrapValue * 1.5, 1.8);
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
                        Plugin.networkHandler.s_moaiDestroyBody.SendAllClients(new moaiDestroyBodyPkg(NetworkObject.NetworkObjectId, ply2.NetworkObject.NetworkObjectId));
                    }
                }
            }

            // consumption
            GrabbableObject obj = getObj();
            PlayerControllerB ply = getPlayerCorpse();

            if ((obj == null && ply == null) || goodBoy > 0)
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
                            Plugin.networkHandler.s_moaiAttachBody.SendAllClients(new moaiAttachBodyPkg(NetworkObject.NetworkObjectId, ply.NetworkObject.NetworkObjectId));
                            Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
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
                                Plugin.networkHandler.s_moaiAttachBody.SendAllClients(new moaiAttachBodyPkg(NetworkObject.NetworkObjectId, ply.NetworkObject.NetworkObjectId));
                                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEatHuman"));
                            }
                            eatingHuman = true;
                        }
                        else if (!eatingScrap)
                        {
                            eatingTimer = (int)(obj.scrapValue / 1.8) + 15;
                            Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureEat"));
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
        }

        public void updateEntranceChance()
        {
            if (!nearestEntrance)
            {
                return;
            }
            var dist = Vector3.Distance(transform.position, nearestEntrance.transform.position);

            chanceToLocateEntrancePlayerBonus = 1;
            if (mostRecentPlayer)
            {
                if (mostRecentPlayer == this.isOutside)
                {
                    chanceToLocateEntrancePlayerBonus = 1;
                }
                else
                {
                    chanceToLocateEntrancePlayerBonus = 1.5f;
                }
            }
            var m1 = 1;

            if (dist < 20) { m1 = 4; };
            if (dist < 15) { m1 = 6; };
            if (dist < 10) { m1 = 7; };
            if (dist < 5) { m1 = 10; }

            if (nearestEntrance)
            {
                chanceToLocateEntrance = (float)(1 / Math.Pow(dist, 2)) * m1 * chanceToLocateEntrancePlayerBonus - entranceDelay;
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
            //Debug.Log("MOAIGUARD: Picking Guard Node");
            List<GameObject> allGoodNodes = new List<GameObject>();

            foreach (GameObject g in allAINodes)
            {
                for (int i = 0; i < StartOfRound.Instance.allPlayerScripts.Length; i++)
                {
                    Vector3 playerPos = StartOfRound.Instance.allPlayerScripts[i].gameObject.transform.position;
                    //Debug.Log(playerPos);
                    //Debug.Log(g.transform.position);
                    float dist = Vector3.Distance(g.transform.position, playerPos);
                    //Debug.Log("Dist: " + dist);
                    //Debug.Log("dist < " + (23 + this.transform.localScale.x));
                    if (dist < (23 + this.transform.localScale.x) && !StartOfRound.Instance.allPlayerScripts[i].isPlayerDead)
                    {
                        allGoodNodes.Add(g);
                        //Debug.Log("appended to good node -> " + allGoodNodes.Count + " - " + allGoodNodes.ToString());
                    }
                }
            }
            if (allGoodNodes.Count > 0)
            {
                //Debug.Log("MOAIGUARD: Returning Good Node");
                return allGoodNodes[UnityEngine.Random.RandomRangeInt(0, allGoodNodes.Count)];
            }
            else
            {
                //Debug.Log("MOAIGUARD: Returning Random Node");
                return allAINodes[UnityEngine.Random.RandomRangeInt(0, allAINodes.Length)];
            }
        }

        public bool FoundClosestPlayerInRange(float r, bool needLineOfSight)
        {
            if(recovering) { return false; }
            moaiTargetClosestPlayer(range: r, requireLineOfSight: needLineOfSight);
            if (targetPlayer == null) return false;
            return targetPlayer != null;
        }

        public bool moaiTargetClosestPlayer(float range, bool requireLineOfSight)
        {
            if (recovering) { return false; }
            mostOptimalDistance = range;
            PlayerControllerB playerControllerB = targetPlayer;
            targetPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                if (PlayerIsTargetable(StartOfRound.Instance.allPlayerScripts[i]) && (!requireLineOfSight || CheckLineOfSightForPosition(StartOfRound.Instance.allPlayerScripts[i].gameplayCamera.transform.position, 100, 80)))
                {
                    tempDist = Vector3.Distance(base.transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                    if (tempDist < mostOptimalDistance)
                    {
                        mostOptimalDistance = tempDist;
                        targetPlayer = StartOfRound.Instance.allPlayerScripts[i];
                    }
                }
            }

            if (targetPlayer != null && playerControllerB != null)
            {
                targetPlayer = playerControllerB;
            }

            return targetPlayer != null;
        }

        EnemyAI ClosestEnemyInRange(float range)
        {
            if (!recovering)
            {
                return null;
            }
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
            if (closestEnemy != null && !closestEnemy.isEnemyDead && closestEnemy.enemyHP > 0 && closestEnemy.enemyType.canDie && closestEnemy.gameObject.activeSelf)
            {
                if (!closestEnemy.gameObject.name.ToLower().Contains("locust"))  // dumb locusts
                {
                    return closestEnemy;
                }
            }
            return null;
        }


        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            if (this.isEnemyDead)
            {
                return;
            }
            this.enemyHP -= force;

            if (playerWhoHit != null)
            {
                provokePoints += 20 * force;
                targetPlayer = playerWhoHit;
            }
            stamina = 60;
            if (base.IsOwner)
            {
                if (this.enemyHP <= 0)
                {
                    base.KillEnemyOnOwnerClient(false);
                    this.stopAllSound();
                    animator.SetInteger("state", 3);
                    isEnemyDead = true;
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureDeath"));
                    return;
                }
                Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureHit"));
            }
        }

        void StickingInFrontOfPlayer()
        {
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
            if (collidedEnemy.gameObject.name.ToLower().Contains("moai"))
            {
                // halos don't hit halos, non-halos don't hit non-halos
                if (transform.Find("Halo").gameObject.activeSelf == collidedEnemy.transform.Find("Halo").gameObject.activeSelf)
                {
                    return;
                }
            }
            this.timeSinceHittingLocalPlayer = 0f;
            collidedEnemy.HitEnemy(1, null, true);
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
                if (!transform.Find("Halo") || !transform.Find("Halo").gameObject.activeSelf)
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
                    if (transform.Find("Halo").gameObject.activeSelf && playerControllerB.health <= 90)
                    {
                        playerControllerB.DamagePlayer(-10);
                    }
                }
            }
        }

        // method to play a sound with a target string id
        // can be overridden in moai variants (thus it is usable in MoaiNormalNet)
        public virtual void playSoundId(String id) { }

        public void stopAllSound()
        {
            // normal creature sounds
            creatureSFX.Stop();
            creatureVoice.Stop();
            creatureEat.Stop();
            creatureEatHuman.Stop();
            creatureFood.Stop();
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

        public void slidingSoundTick()
        {
            if(isEnemyDead || agent.velocity.magnitude < (agent.speed / 8))
            {
                if (isSliding)
                {
                    isSliding = false;
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "stopSliding"));
                }
            }

            var slideMaterial = getCurrentMaterialSittingOn();
            switch(slideMaterial)
            {
                default:
                    if (!slidingBasic.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingBasic"));
                    }
                    break;
                case "Gravel":
                    if (!slidingGravel.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingGravel"));
                    }
                    break;
                case "CatWalk":
                    if (!slidingMetal.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingMetal"));
                    }
                    break;
                case "Aluminum":
                    if (!slidingMetal.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingMetal"));
                    }
                    break;
                case "Dirt":
                    if (!slidingGravel.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingGravel"));
                    }
                    break;
                case "Snow":
                    if (!slidingSnow.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingSnow"));
                    }
                    break;
                case "Carpet":
                    if (!slidingSnow.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingSnow"));
                    }
                    break;
                case "Wood":
                    if (!slidingWood.isPlaying)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "slidingWood"));
                    }
                    break;
                case "None":
                    Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "stopSliding"));
                    break;
            }
        }

        public void stopSlideSounds()
        {
            slidingBasic.Stop();
            slidingGravel.Stop();
            slidingMetal.Stop();
            slidingSnow.Stop();
            slidingWood.Stop();
        }

        public String getCurrentMaterialSittingOn()
        {
            var slidingSurface = "None";
            var interactRay = new Ray(this.transform.position + Vector3.up, -Vector3.up);
            RaycastHit castHit;
            if (Physics.Raycast(interactRay, out castHit, 6f, StartOfRound.Instance.walkableSurfacesMask, QueryTriggerInteraction.Ignore))
            {
                for (int i = 0; i < StartOfRound.Instance.footstepSurfaces.Length; i++)
                {
                    // go through all surfaces
                    if (castHit.collider.CompareTag(StartOfRound.Instance.footstepSurfaces[i].surfaceTag))
                    {
                        slidingSurface = StartOfRound.Instance.footstepSurfaces[i].surfaceTag;
                    }
                }
            }
            //Debug.Log("MOAI: Sliding Surface = " + slidingSurface);
            return slidingSurface;
        }

        [ClientRpc]
        // note that this is only for rotation animations (cause its moai)
        // these are synced through a network transform
        public void DoAnimationClientRpc(int index)
        {
            //LogIfDebugBuild($"Animation: {index}");
            if (RoundManager.Instance.IsServer)
            {
                if (this.animator) { this.animator.SetInteger("state", index); }
            }
        }
    }
}
