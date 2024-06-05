using System;
using System.Collections;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UIElements;
using static MoaiEnemy.Plugin;
using System.Threading.Tasks;
using System.Linq;
using static MoaiEnemy.src.MoaiNormal.MoaiNormalNet;
using LethalLib.Modules;

namespace MoaiEnemy.src.MoaiNormal
{

    class RedEnemyAI : MOAIAICORE
    {
        // updated once every 15 seconds
        bool preparing = false;
        float anger = 0; 

        // blitz vars
        Vector3 blitzTarget = Vector3.zero;
        Vector3 startPosFromTarget = Vector3.zero;
        int playerTargetSteps = 0;
        int tempHp = 0;

        // kidnap vars
        private PlayerControllerB playerHeld = null;
        bool afterEntranceTransport = false;
        GameObject kidnapNode = null;
        int invalidCounter = 0;
        public Transform playerGrabPoint;

        // extra audio sources
        public AudioSource creaturePrepare;
        public AudioSource creatureBlitz;
        public AudioSource creatureKidnap;
        public GameObject flameEffect;
        public GameObject swirlEffect;

        new enum State
        {
            // defaults
            SearchingForPlayer,
            Guard,
            StickingInFrontOfEnemy,
            StickingInFrontOfPlayer,
            HeadSwingAttackInProgress,
            HeadingToEntrance,
            //define custom below
            Preparing,
            Blitz,
            Kidnapping
        }

        public override void Start()
        {
            baseInit();
            creatureBlitz.volume = moaiGlobalMusicVol.Value;
            creaturePrepare.volume = moaiGlobalMusicVol.Value;
            creatureKidnap.volume = moaiGlobalMusicVol.Value;
            flameEffect.SetActive(false);
        }

        public override void setPitches(float pitchAlter)
        {
            creatureBlitz.pitch /= pitchAlter;
            creaturePrepare.pitch /= pitchAlter;
            creatureKidnap.pitch /= pitchAlter;
        }

        public override void Update()
        {
            base.Update();
            baseUpdate();

            if (isEnemyDead && playerHeld)
            {
                letGoOfPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId);
                kidnapNode = null;
            }

            switch (currentBehaviourStateIndex)
            {
                case (int)State.Preparing:
                    if (!flameEffect.activeInHierarchy) { flameEffect.SetActive(true); }
                    break;
                case (int)State.Blitz:
                    if (!flameEffect.activeInHierarchy) { flameEffect.SetActive(true); }
                    break;
                default:
                    if (flameEffect.activeInHierarchy) { flameEffect.SetActive(false); }
                    break;
             }
        }

        public override void playSoundId(String id)
        {
            switch (id)
            {
                case "creatureBlitz":
                    stopAllSound();
                    creatureBlitz.Play();
                    break;
                case "creaturePrepare":
                    stopAllSound();
                    creaturePrepare.Play();
                    break;
                case "creatureKidnap":
                    stopAllSound();
                    creatureKidnap.Play();
                    break;
            }
        }

        public bool playerIsAlone(PlayerControllerB player)
        {
            RoundManager m = RoundManager.Instance;
            var team = RoundManager.Instance.playersManager.allPlayerScripts;
            for (int i = 0; i < team.Length; i++)
            {
                var p = team[i];
                if(p.playerClientId != player.playerClientId)
                {
                    // test distance
                    if(Vector3.Distance(p.transform.position, player.transform.position) < 30)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public bool playerOnRock(PlayerControllerB player)
        {
            var slidingSurface = "None";
            var interactRay = new Ray(player.transform.position + Vector3.up, -Vector3.up);
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
            switch (slidingSurface)
            {
                default:
                    return false;
                case "Rock":
                    return true;
                case "Concrete":
                    return true;
            }
        }

        public bool playerIsDefenseless(PlayerControllerB player)
        {
            var items = player.ItemSlots;

            if(player.carryWeight >= 1.38)
            {
                return false;
            }

            for(int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item && item.itemProperties && item.itemProperties.isDefensiveWeapon)
                {
                    return false;
                }
            }
            return true;
        }

        public override void DoAIInterval()
        {
            if (isEnemyDead)
            {
                return;
            };
            base.DoAIInterval();
            baseAIInterval();

            if(currentBehaviourStateIndex == (int)State.HeadSwingAttackInProgress)
            {
                if (playerHeld)
                {
                    letGoOfPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId);
                }
            }

            if (agent.velocity.magnitude < 1 && playerHeld)
            {
                invalidCounter++;
                if(invalidCounter > 30 && playerHeld)
                {
                    letGoOfPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId);
                }
            }
            else
            {
                if (invalidCounter > 0)
                {
                    invalidCounter--;
                }
            }

            switch (currentBehaviourStateIndex)
            {
                case (int)State.SearchingForPlayer:
                    Debug.Log("SearchingForPlayer");

                    // in this case, the player has entered the factory with the red moai
                    if (playerHeld)
                    {
                        Debug.Log("RED MOAI: Switching back to kidnapping state -> continuation:: ");
                        afterEntranceTransport = true;
                        kidnapNode = allAINodes[UnityEngine.Random.RandomRangeInt(0, allAINodes.Length)];
                        SwitchToBehaviourClientRpc((int)State.Kidnapping);
                        return;
                    }

                    if (FoundClosestPlayerInRange(40f, true) || provokePoints > 0)  // sets targetPlayer when true
                    {
                        // stare state transfer
                        if (anger < 100)
                        {
                            if (!swirlEffect.activeInHierarchy) { toggleSwirlEffectClientRpc(true); }
                            
                            agent.speed = 0.01f;
                            if(playerOnRock(targetPlayer))
                            {
                                anger += 20;
                            }
                            else
                            {   // anger builds up faster the closer you are
                                var logResult = (float)Math.Log(45 / Vector3.Distance(transform.position, targetPlayer.transform.position), 1.3); ;
                                if (logResult > 2)
                                {
                                    anger += logResult;
                                }
                                else
                                {
                                    anger += 2;
                                }
                            }
                            return;
                        }
                        if (swirlEffect.activeInHierarchy) { toggleSwirlEffectClientRpc(false); }

                        // kidnap state transfer
                        if (anger >= 100 && playerIsAlone(targetPlayer) && playerIsDefenseless(targetPlayer) && !playerOnRock(targetPlayer) && (transform.localScale.x > 0.3) && (transform.localScale.x < 2.5))
                        {
                            Debug.Log("Swithing to Kidnap State");
                            StopSearch(currentSearch);
                            SwitchToBehaviourClientRpc((int)State.Kidnapping);
                            anger = 0;
                            Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureKidnap"));
                            return;
                        }

                        if (anger >= 100)
                        {
                            // blitz state transfer
                            Debug.Log("Swithing to Preparing State");
                            StopSearch(currentSearch);
                            tempHp = enemyHP;
                            SwitchToBehaviourClientRpc((int)State.Preparing);
                            anger = 0;
                            return;
                        }
                    }
                    else
                    {
                        if (swirlEffect.activeInHierarchy) { toggleSwirlEffectClientRpc(false); }
                        if (anger > 0)
                        {
                            anger -= 1.3f;  // 20 seconds from 100 anger to completely deaggro
                        }
                    }
                    baseSearchingForPlayer();
                    break;
                case (int)State.Kidnapping:
                    agent.speed = 20f * moaiGlobalSpeed.Value;
                    agent.acceleration = 80;
                    agent.angularSpeed = 1200;

                    // sub state - Getting to player
                    if(!playerHeld)
                    {
                        Debug.Log("Transporting Player");
                        // reset if player is lost
                        if(!FoundClosestPlayerInRange(40f, true))
                        {
                            tempHp = enemyHP;
                            blitzReset();
                            break;
                        }

                        // attach player if they are close enough
                        if (Vector3.Distance(transform.position, targetPlayer.transform.position) < targetPlayer.transform.localScale.magnitude + Math.Max(transform.localScale.magnitude, 1))
                        {
                            playerHeld = targetPlayer;
                            Debug.Log("RED MOAI: Attaching living player to mouth.");
                            attachPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId, true);
                        }
                    }
                    else
                    {
                        Debug.Log("RED MOAI: PlayerHeld -> " + !playerHeld.isInsideFactory);

                        // to ensure the player stays attached...
                        attachPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId);

                        // substate - Transporting Player
                        if (!afterEntranceTransport)
                        {
                            Debug.Log("RED MOAI: Transporting to Factory...");
                            // transport player to factory first!
                            baseHeadingToEntrance();
                        }
                        else if (kidnapNode && !agent.isPathStale)
                        {
                            Debug.Log("RED MOAI: searching for kidnapNode -> " + kidnapNode);
                            agent.speed = 10f * moaiGlobalSpeed.Value;
                            agent.acceleration = 80;
                            agent.angularSpeed = 1200;
                            SetDestinationToPosition(kidnapNode.transform.position);
                            if((Vector3.Distance(transform.position, kidnapNode.transform.position) < 2 + transform.localScale.magnitude))
                            {
                                kidnapNode = null;
                            }
                        }
                        else
                        {
                            Debug.Log("RED MOAI: Let go of player -> " + agent.isPathStale + " -- " + kidnapNode);
                            // for now... let the player go
                            letGoOfPlayerClientRpc(playerHeld.NetworkObject.NetworkObjectId);
                        }
                    }
                    break;
                case (int)State.HeadingToEntrance:
                    baseHeadingToEntrance();
                    break;
                case (int)State.Guard:
                    baseGuard();
                    break;
                case (int)State.StickingInFrontOfEnemy:
                    baseStickingInFrontOfEnemy();
                    break;
                case (int)State.StickingInFrontOfPlayer:
                    baseStickingInFrontOfPlayer();
                    break;

                case (int)State.HeadSwingAttackInProgress:
                    baseHeadSwingAttackInProgress();
                    break;
                case (int)State.Preparing:
                    agent.speed = 0;

                    // Transition to Blitz if sound is no longer playing
                    if (creaturePrepare.time > 3.7)
                    {
                        LogIfDebugBuild("MOAIRED: Blitz Activated");
                        preparing = false;
                        playerTargetSteps = 0;
                        impatience = 0;
                        SwitchToBehaviourClientRpc((int)State.Blitz);
                        tempHp = enemyHP;
                        return;
                    }

                    // sound switch 
                    if (!preparing)
                    {
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creaturePrepare"));
                        preparing = true;
                    }
                    break;
                case (int)State.Blitz:
                    agent.speed = 35f * moaiGlobalSpeed.Value;
                    agent.acceleration = 80;
                    agent.angularSpeed = 1200;
                    enemyHP = 500;

                    // in blitz, the target resets if blitzTarget is Vector3.zero
                    if (blitzTarget == Vector3.zero)
                    {
                        impatience = 0;
                        var player = GetClosestPlayer(false, true, false);
                        NavMeshHit hit;
                        if (!player)
                        {
                            StartSearch(transform.position);
                            blitzReset();
                            return;
                        }
                        Vector3 playerPos = player.gameObject.transform.position;
                        Vector3 rayDisposition = transform.position - playerPos;
                        Vector3 scaledRayDisposition = Vector3.Normalize(rayDisposition) * Math.Min(Vector3.Distance(playerPos, transform.position), UnityEngine.Random.Range(7f, 9.0f));
                        var valid = UnityEngine.AI.NavMesh.SamplePosition(playerPos - scaledRayDisposition, out hit, 10f, NavMesh.AllAreas);

                        if(!valid)
                        {
                            Debug.Log("MOAI RED: Position Sample Failure -> " + playerPos + " -- " + rayDisposition);
                            blitzReset();
                            return;
                        }
                        else
                        {
                            Debug.Log("MOAI RED: Position Sample Success -> " + hit.position);
                        }

                        blitzTarget = hit.position;
                        Plugin.networkHandler.s_moaiSoundPlay.SendAllClients(new moaiSoundPkg(NetworkObject.NetworkObjectId, "creatureBlitz"));
                        spawnExplosionClientRpc();
                        startPosFromTarget = this.transform.position;

                        if (playerTargetSteps >= 3)
                        {
                            Debug.Log("red: target end");
                            blitzReset();
                            return;
                        }
                        else
                        {
                            playerTargetSteps++;
                            Debug.Log("red: target player");
                        }
                    }

                    targetPlayer = null;
                    SetDestinationToPosition(blitzTarget);

                    // blitz reset
                    if (Vector3.Distance(transform.position, blitzTarget) < (transform.localScale.magnitude + transform.localScale.magnitude + impatience))
                    {
                        blitzTarget = Vector3.zero;
                    }
                    else
                    {
                        impatience += 0.1f;
                    }
                    break;

                default:
                    LogIfDebugBuild("This Behavior State doesn't exist!");
                    break;
            }
        }

        public void blitzReset()
        {
            StartSearch(transform.position);
            SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            stamina = 0;
            enemyHP = tempHp;
            blitzTarget = Vector3.zero;
        }

        [ClientRpc]
        public void spawnExplosionClientRpc()
        {
            Landmine.SpawnExplosion(transform.position + UnityEngine.Vector3.up, true, 5.7f, 6.4f);
        }

        [ClientRpc]
        public void attachPlayerClientRpc(ulong playerId, bool healPlayer = false)
        {
            RoundManager m = RoundManager.Instance;
            PlayerControllerB[] players = m.playersManager.allPlayerScripts;
            for(int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player.NetworkObject.NetworkObjectId == playerId)
                {
                    player.transform.position = playerGrabPoint.position;
                    player.transform.parent = playerGrabPoint;
                    player.playerCollider.enabled = false;
                    if(healPlayer) { player.DamagePlayer(-30); }
                    return;
                }
            }
        }

        [ClientRpc]
        public void letGoOfPlayerClientRpc(ulong playerId)
        {
            PlayerControllerB targetPlayer = null;
            RoundManager m = RoundManager.Instance;
            PlayerControllerB[] players = m.playersManager.allPlayerScripts;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player.NetworkObject.NetworkObjectId == playerId)
                {
                    targetPlayer = player;
                }

            }
            targetPlayer.transform.parent = null;
            targetPlayer.playerCollider.enabled = true;

            // the player needs to be on a navmesh spot (avoiding all collision bugs)
            NavMeshHit hit;
            var valid = UnityEngine.AI.NavMesh.SamplePosition(targetPlayer.transform.position, out hit, 15f, NavMesh.AllAreas);
            if (valid)
            {
                targetPlayer.transform.position = hit.position;
            }

            if (RoundManager.Instance.IsServer)
            {
                blitzReset();
                tempHp = enemyHP;
                afterEntranceTransport = false;
                playerHeld = null;
            }
        }

        [ClientRpc]
        public void toggleSwirlEffectClientRpc(bool value)
        {
            swirlEffect.SetActive(value);
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            if (this.isEnemyDead)
            {
                return;
            }

            if (playerWhoHit != null)
            {
                provokePoints += 20 * force;
                stamina = 60;
                anger = Math.Max(100, anger + force * 50);
            }
        }
    }
}